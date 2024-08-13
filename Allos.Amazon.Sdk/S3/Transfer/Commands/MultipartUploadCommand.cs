﻿using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Internal;
using Amazon.S3.Model;
using Amazon.Sdk.Fork;
using Amazon.Sdk.S3.Util;
using Amazon.Util;
using Serilog;

namespace Amazon.Sdk.S3.Transfer.Internal
{
    /// <summary>
    /// The command to manage an upload using the S3 multipart API.
    /// </summary>
    [AmazonSdkFork("sdk/src/Services/S3/Custom/Transfer/Internal/MultipartUploadCommand.cs", "Amazon.S3.Transfer.Internal")]
    [AmazonSdkFork("sdk/src/Services/S3/Custom/Transfer/Internal/_async/MultipartUploadCommand.async.cs", "Amazon.S3.Transfer.Internal")]
    internal class MultipartUploadCommand : BaseCommand
    {
        private readonly IAmazonS3 _s3Client;
        private readonly long _partSize;
        private int _totalNumberOfParts;
        private readonly TransferUtilityConfig _config;
        private readonly TransferUtilityUploadRequest _fileTransporterRequest;
        private readonly ConcurrentDictionary<uint, Stream> _inputStreams;

        private List<UploadPartResponse> _uploadResponses = new();
        private ulong _totalTransferredBytes;
        private readonly Queue<UploadPartRequest> _partsToUpload = new();
        
        private readonly ulong? _contentLength;
        private static ILogger Logger => TonicLogger.ForContext<AsyncTransferUtility>();

        /// <summary>
        /// Initializes a new instance of the <see cref="MultipartUploadCommand"/> class.
        /// </summary>
        /// <param name="s3Client">The s3 client.</param>
        /// <param name="config">The config object that has the number of threads to use.</param>
        /// <param name="fileTransporterRequest">The file transporter request.</param>
        internal MultipartUploadCommand(
            IAmazonS3 s3Client, 
            TransferUtilityConfig config, 
            TransferUtilityUploadRequest fileTransporterRequest)
        {
            if (fileTransporterRequest.IsSetFilePath())
            {
                Logger.Debug("Beginning upload of file `{FilePath}`", 
                    fileTransporterRequest.FilePath);
            }
            else if (fileTransporterRequest.IsSetInputStream())
            {
                Logger.Debug("Beginning upload of `{StreamType}`", 
                    fileTransporterRequest.InputStream.GetType().FullName);
            }
            else
            {
                throw new ArgumentException(
                    $"One of `{nameof(fileTransporterRequest.FilePath)}` or `{nameof(fileTransporterRequest.InputStream)}` must be provided");
            }

            _inputStreams = new ConcurrentDictionary<uint, Stream>();
            _config = config;
            _s3Client = s3Client;
            _fileTransporterRequest = fileTransporterRequest;
            _contentLength = _fileTransporterRequest.ContentLength;

            _partSize = fileTransporterRequest.IsSetPartSize() ? 
                fileTransporterRequest.PartSize : 
                CalculatePartSize(_contentLength);

            if (fileTransporterRequest.InputStream != null)
            {
                if (fileTransporterRequest.AutoResetStreamPosition && fileTransporterRequest.InputStream.CanSeek)
                {
                    fileTransporterRequest.InputStream.Seek(0, SeekOrigin.Begin);
                }
            }

            Logger.Debug("Upload part size {PartSize}", _partSize);
        }
        
        public SemaphoreSlim? AsyncThrottler { get; set; }

        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            if (_fileTransporterRequest.InputStream is { CanSeek: false } || 
                 !_fileTransporterRequest.ContentLength.HasValue)
            {
                await UploadUnseekableStreamAsync(_fileTransporterRequest, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ArgumentNullException.ThrowIfNull(_fileTransporterRequest.ContentLength);
                
                var initRequest = ConstructInitiateMultipartUploadRequest();
                var initResponse = await _s3Client.InitiateMultipartUploadAsync(initRequest, cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                
                Logger.Debug("Initiated upload: {UploadId}", 
                    initResponse.UploadId);

                var pendingUploadPartTasks = new List<Task<UploadPartResponse>>();
                var pendingUploadPartTaskContinuations = new List<Task>();

                SemaphoreSlim? localThrottler = null;
                CancellationTokenSource? internalCts = null;
                try
                {
                    Logger.Debug("Queue up the {Request}s to be executed", 
                        nameof(UploadPartRequest));
                    
                    var contentLengthLong = Convert.ToInt64(_contentLength);
                    
                    long filePosition = 0;
                    
                    for (uint i = 1; filePosition < contentLengthLong; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var uploadRequest = ConstructUploadPartRequest(i, filePosition, initResponse);
                        _partsToUpload.Enqueue(uploadRequest);
                        filePosition += _partSize;
                    }

                    _totalNumberOfParts = _partsToUpload.Count;

                    Logger.Debug("Scheduling the {TotalNumberOfParts} UploadPartRequests in the queue",
                        _totalNumberOfParts);

                    internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var concurrencyLevel = CalculateConcurrentServiceRequests();
                    localThrottler = AsyncThrottler ?? new SemaphoreSlim(concurrencyLevel);

                    foreach (var uploadRequest in _partsToUpload)
                    {
                        await localThrottler.WaitAsync(cancellationToken)
                            .ConfigureAwait(continueOnCapturedContext: false);

                        cancellationToken.ThrowIfCancellationRequested();
                        if (internalCts.IsCancellationRequested)
                        {
                            // Operation cancelled as one of the UploadParts requests failed with an exception,
                            // don't schedule anymore UploadPart tasks.
                            // Don't throw an OperationCanceledException here as we want to process the 
                            // responses and throw the original exception.
                            break;
                        }

                        var task = UploadPartAsync(uploadRequest, internalCts, localThrottler);
                        
                        pendingUploadPartTasks.Add(task);

                        var continuation = task.ContinueWith(t =>
                        {
                            _inputStreams.TryRemove((uint)uploadRequest.PartNumber, out var inputStream);
                            
                            inputStream?.Dispose();
                            
                        }, internalCts.Token);
                        
                        pendingUploadPartTaskContinuations.Add(continuation);
                    }

                    Logger.Debug("Waiting for upload part requests to complete `{UploadId}`", 
                        initResponse.UploadId);
                    _uploadResponses = await WhenAllOrFirstExceptionAsync(pendingUploadPartTasks, cancellationToken)
                        .ConfigureAwait(continueOnCapturedContext: false);

                    Logger.Debug("Beginning completing multipart `{UploadId}`", 
                        initResponse.UploadId);
                    var compRequest = ConstructCompleteMultipartUploadRequest(initResponse);
                    await _s3Client.CompleteMultipartUploadAsync(compRequest, cancellationToken)
                        .ConfigureAwait(continueOnCapturedContext: false);
                    Logger.Debug("Done completing multipart `{UploadId}`", 
                        initResponse.UploadId);

                }
                catch (Exception e)
                {
                    Logger.Error(e, "Exception while uploading `{UploadId}`", 
                        initResponse.UploadId);
                    // Can't do async invocation in the catch block, doing cleanup synchronously.
                    Cleanup(initResponse.UploadId, pendingUploadPartTasks);
                    throw;
                }
                finally
                {
                    if (internalCts != null)
                        internalCts.Dispose();

                    if (localThrottler != null && localThrottler != AsyncThrottler)
                        localThrottler.Dispose();

                    if (_fileTransporterRequest.InputStream != null && 
                        !_fileTransporterRequest.IsSetFilePath() && 
                        _fileTransporterRequest.AutoCloseStream)
                    {
                        await _fileTransporterRequest.InputStream.DisposeAsync().ConfigureAwait(false);
                    }

                    //continuations are intended to clean up input streams,
                    //but that may happen, happen partially, or not happen at all
                    //what is left of the input streams are explicitly cleared below
                    pendingUploadPartTaskContinuations.Clear();
                    
                    foreach (var inputStream in _inputStreams.Values)
                    {
                        await inputStream.DisposeAsync().ConfigureAwait(false);
                    }
                    _inputStreams.Clear();
                } 
            }
        }

        private async Task<UploadPartResponse> UploadPartAsync(
            UploadPartRequest uploadRequest, 
            CancellationTokenSource internalCts, 
            SemaphoreSlim asyncThrottler)
        {
            try
            {
                return await _s3Client.UploadPartAsync(uploadRequest, internalCts.Token)
                    .ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (Exception exception)
            {
                if (!(exception is OperationCanceledException))
                {
                    // Cancel scheduling any more tasks
                    // Cancel other UploadPart requests running in parallel.
                    await internalCts.CancelAsync().ConfigureAwait(false);
                }
                throw;
            }
            finally
            {
                asyncThrottler.Release();
            }
        }       

        private void Cleanup(string uploadId, List<Task<UploadPartResponse>> tasks)
        {
            try
            {
                // Make sure all tasks complete (to completion/faulted/cancelled).
                Task.WaitAll(tasks.Cast<Task>().ToArray(), 5000); 
            }
            catch(Exception exception)
            {
                Logger.Information(
                    exception,
                    "A timeout occured while waiting for all upload part request to complete as part of aborting the multipart upload : {ErrorMessage}",
                    exception.Message);
            }
            AbortMultipartUpload(uploadId);
        }

        private void AbortMultipartUpload(string uploadId)
        {
            try
            {
                _s3Client.AbortMultipartUploadAsync(new()
                {
                    BucketName = _fileTransporterRequest.BucketName,
                    Key = _fileTransporterRequest.Key,
                    UploadId = uploadId
                }).Wait();
            }
            catch (Exception e)
            {
                Logger.Information("Error attempting to abort multipart for key `{ObjectKey}`: {ErrorMessage}", 
                    _fileTransporterRequest.Key, 
                    e.Message);
            }
        }
        private async Task UploadUnseekableStreamAsync(
            TransferUtilityUploadRequest request, 
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request.InputStream);
            
            int readBufferSize = _s3Client.Config.BufferSize;

            RequestEventHandler requestEventHandler = (_, args) =>
            {
                if (args is WebServiceRequestEventArgs wsArgs)
                {
                    string currentUserAgent = wsArgs.Headers[AWSSDKUtils.UserAgentHeader];
                    wsArgs.Headers[AWSSDKUtils.UserAgentHeader] =
                        currentUserAgent + " ft/s3-transfer md/UploadNonSeekableStream";
                }
            };

            var initiateRequest = ConstructInitiateMultipartUploadRequest(requestEventHandler);
            var initiateResponse = await _s3Client.InitiateMultipartUploadAsync(initiateRequest, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                // if partSize is not specified on the request, the default value is 0
                long minPartSize = request.PartSize != 0 ? request.PartSize : S3Constants.MinPartSize;
                var uploadPartResponses = new List<UploadPartResponse>();
                var readBuffer = ArrayPool<byte>.Shared.Rent(readBufferSize);
                var partBuffer = ArrayPool<byte>.Shared.Rent((int)minPartSize + (readBufferSize));
                MemoryStream nextUploadBuffer = new(partBuffer);
                await using (var stream = request.InputStream)
                {
                    try
                    {
                        int partNumber = 1;
                        int readBytesCount, readAheadBytesCount;

                        readBytesCount = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, cancellationToken)
                            .ConfigureAwait(false);

                        do
                        {
                            await nextUploadBuffer.WriteAsync(readBuffer, 0, readBytesCount, cancellationToken)
                                .ConfigureAwait(false);
                            // read the stream ahead and process it in the next iteration.
                            // this is used to set isLastPart when there is no data left in the stream.
                            readAheadBytesCount = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, cancellationToken)
                                .ConfigureAwait(false);

                            if ((nextUploadBuffer.Position > minPartSize || readAheadBytesCount == 0))
                            {
                                if (nextUploadBuffer.Position == 0)
                                {
                                    if (partNumber == 1)
                                    {
                                        // if the input stream is empty then upload empty MemoryStream.
                                        // without doing this the UploadPart call will use the length of the
                                        // nextUploadBuffer as the pastSize. The length will be incorrectly computed
                                        // for the part as (int)minPartSize + (READ_BUFFER_SIZE) as defined above for partBuffer.
                                        await nextUploadBuffer.DisposeAsync().ConfigureAwait(false);
                                        nextUploadBuffer = new();
                                    }
                                }
                                bool isLastPart = readAheadBytesCount == 0;

                                var partSize = nextUploadBuffer.Position;
                                nextUploadBuffer.Position = 0;
                                
                                UploadPartRequest uploadPartRequest = ConstructUploadPartRequestForNonSeekableStream(
                                    nextUploadBuffer, 
                                    partNumber, 
                                    partSize, 
                                    isLastPart, 
                                    initiateResponse);

                                var partResponse = await _s3Client.UploadPartAsync(uploadPartRequest, cancellationToken).ConfigureAwait(false);
                                Logger.Debug(
                                    "Uploaded part {PartNumber} (PartSize={PartSize} UploadId={UploadId} IsLastPart={IsLastPart})", 
                                    partNumber, 
                                    partSize, 
                                    initiateResponse.UploadId,
                                    isLastPart);
                                uploadPartResponses.Add(partResponse);
                                partNumber++;

                                await nextUploadBuffer.DisposeAsync().ConfigureAwait(false);
                                nextUploadBuffer = new(partBuffer);
                            }
                            readBytesCount = readAheadBytesCount;
                        }
                        while (readAheadBytesCount > 0);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(partBuffer);
                        ArrayPool<byte>.Shared.Return(readBuffer);
                        await nextUploadBuffer.DisposeAsync().ConfigureAwait(false);
                    }

                    _uploadResponses = uploadPartResponses;
                    CompleteMultipartUploadRequest compRequest = ConstructCompleteMultipartUploadRequest(
                        initiateResponse, 
                        true,
                        requestEventHandler);
                    await _s3Client.CompleteMultipartUploadAsync(compRequest, cancellationToken).ConfigureAwait(false);
                    Logger.Debug("Completed multi part upload (PartCount={PartCount}, UploadId={UploadId})",
                        uploadPartResponses.Count, 
                        initiateResponse.UploadId);
                }
            }
            catch (Exception ex)
            {
                await _s3Client.AbortMultipartUploadAsync(new()
                {
                    BucketName = request.BucketName,
                    Key = request.Key,
                    UploadId = initiateResponse.UploadId
                }, cancellationToken).ConfigureAwait(false);
                Logger.Error(ex, ex.Message);
                throw;
            }
        }

        private static long CalculatePartSize(ulong? fileSize)
        {
            if(fileSize == null)
            {
                return S3Constants.MinPartSize;
            }
            double partSize = Math.Ceiling((double)fileSize / S3Constants.MaxNumberOfParts);
            if (partSize < S3Constants.MinPartSize)
            {
                partSize = S3Constants.MinPartSize;
            }

            return (long)partSize;
        }

        private string? DetermineContentType()
        {
            if (_fileTransporterRequest.IsSetContentType())
                return _fileTransporterRequest.ContentType;

            if (_fileTransporterRequest.IsSetFilePath() ||
                _fileTransporterRequest.IsSetKey())
            {
                // Get the extension of the file from the path.
                // Try the key as well.
                string ext = AWSSDKUtils.GetExtension(_fileTransporterRequest.FilePath);
                if (string.IsNullOrWhiteSpace(ext) &&
                    _fileTransporterRequest.IsSetKey())
                {
                    ext = AWSSDKUtils.GetExtension(_fileTransporterRequest.Key);
                }

                string type = AmazonS3Util.MimeTypeFromExtension(ext);
                return type;
            }
            return null;
        }

        private int CalculateConcurrentServiceRequests()
        {
            int threadCount;
            if (_fileTransporterRequest.IsSetFilePath()
                && !(_s3Client is IAmazonS3Encryption))
            {
                threadCount = _config.ConcurrentServiceRequests;
            }
            else
            {
                threadCount = 1; // When using streams or encryption, multiple threads can not be used to read from the same stream.
            }

            if (_totalNumberOfParts < threadCount)
            {
                threadCount = _totalNumberOfParts;
            }
            return threadCount;
        }

        private CompleteMultipartUploadRequest ConstructCompleteMultipartUploadRequest(
            InitiateMultipartUploadResponse initResponse
            ) => 
            ConstructCompleteMultipartUploadRequest(initResponse, false, null);

        private CompleteMultipartUploadRequest ConstructCompleteMultipartUploadRequest(
            InitiateMultipartUploadResponse initResponse, 
            bool skipPartValidation, 
            RequestEventHandler? requestEventHandler)
        {
            if (!skipPartValidation)
            {
                if (_uploadResponses.Count != _totalNumberOfParts)
                {
                    throw new InvalidOperationException(
                        $"Cannot complete multipart upload request. The total number of completed parts ({_uploadResponses.Count}) " +
                        $"does not equal the total number of parts created ({_totalNumberOfParts})");
                }
            }

            var compRequest = new CompleteMultipartUploadRequest
            {
                BucketName = _fileTransporterRequest.BucketName,
                Key = _fileTransporterRequest.Key,
                UploadId = initResponse.UploadId
            };

            if(_fileTransporterRequest.ServerSideEncryptionCustomerMethod != null 
                && _fileTransporterRequest.ServerSideEncryptionCustomerMethod != ServerSideEncryptionCustomerMethod.None)
            {
                compRequest.SSECustomerAlgorithm = _fileTransporterRequest.ServerSideEncryptionCustomerMethod.ToString();
            }

            compRequest.AddPartETags(_uploadResponses);

            ((IAmazonWebServiceRequest)compRequest).AddBeforeRequestHandler(requestEventHandler ?? RequestEventHandler);

            return compRequest;
        }

        private UploadPartRequest ConstructUploadPartRequest(
            uint partNumber, 
            long filePosition, 
            InitiateMultipartUploadResponse initiateResponse)
        {
            ArgumentNullException.ThrowIfNull(_contentLength);

            var contentLengthLong = Convert.ToInt64(_contentLength);

            ArgumentException.ThrowIfNullOrWhiteSpace(_fileTransporterRequest.FilePath);
            
            var fileInfo = new FileInfo(_fileTransporterRequest.FilePath);

            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException(
                    $"The file `{fileInfo.FullName}` does not exist",
                    nameof(_fileTransporterRequest.FilePath));
            }

            var partFileName = $"{fileInfo.Name}.part{partNumber}";
            string partFileFauxPath;
            
            if (fileInfo.Directory != null)
            {
                partFileFauxPath = Path.Combine(fileInfo.Directory.FullName, partFileName);
            }
            else
            {
                partFileFauxPath = partFileName;
            }
            
            UploadPartRequest uploadPartRequest = ConstructGenericUploadPartRequest(initiateResponse);

            uploadPartRequest.PartNumber = Convert.ToInt32(partNumber);
            uploadPartRequest.PartSize = _partSize;

            if ((filePosition + _partSize >= contentLengthLong)
                && _s3Client is IAmazonS3Encryption)
            {
                uploadPartRequest.IsLastPart = true;
                uploadPartRequest.PartSize = 0;
            }

            var progressHandler = new ProgressHandler(
                (ulong) _s3Client.Config.ProgressUpdateInterval,
                _contentLength,
                partFileFauxPath,
                UploadPartProgressEventCallback
                );
            
            ((IAmazonWebServiceRequest)uploadPartRequest).AddBeforeRequestHandler(RequestEventHandler);

            if (_fileTransporterRequest.IsSetFilePath())
            {
                uploadPartRequest.FilePosition = filePosition;
                uploadPartRequest.FilePath = _fileTransporterRequest.FilePath;
                
                var partInputStream = File.OpenRead(_fileTransporterRequest.FilePath);
                partInputStream.Seek(filePosition, SeekOrigin.Begin);
                _inputStreams.TryAdd(partNumber, partInputStream);
            
                uploadPartRequest.FilePath = null;
                uploadPartRequest.InputStream = partInputStream;
            }
            else
            {
                uploadPartRequest.InputStream = _fileTransporterRequest.InputStream;
            }

            if (uploadPartRequest.InputStream == null)
            {
                ArgumentNullException.ThrowIfNull(uploadPartRequest.InputStream);
            }

            var eventStream = new EventStream(uploadPartRequest.InputStream);
            
            eventStream.OnRead += progressHandler.OnBytesRead;
            
            uploadPartRequest.InputStream = eventStream;

            return uploadPartRequest;
        }

        private UploadPartRequest ConstructGenericUploadPartRequest(InitiateMultipartUploadResponse initiateResponse)
        {
            UploadPartRequest uploadPartRequest = new()
            {
                BucketName = _fileTransporterRequest.BucketName,
                Key = _fileTransporterRequest.Key,
                UploadId = initiateResponse.UploadId,
                ServerSideEncryptionCustomerMethod = _fileTransporterRequest.ServerSideEncryptionCustomerMethod,
                ServerSideEncryptionCustomerProvidedKey = _fileTransporterRequest.ServerSideEncryptionCustomerProvidedKey,
                ServerSideEncryptionCustomerProvidedKeyMD5 = _fileTransporterRequest.ServerSideEncryptionCustomerProvidedKeyMd5,
                DisableDefaultChecksumValidation = _fileTransporterRequest.DisableDefaultChecksumValidation,
                DisablePayloadSigning = _fileTransporterRequest.DisablePayloadSigning,
                ChecksumAlgorithm = _fileTransporterRequest.ChecksumAlgorithm,
                CalculateContentMD5Header = _fileTransporterRequest.CalculateContentMd5Header
            };

            // If the InitiateMultipartUploadResponse indicates that this upload is using KMS, force SigV4 for each UploadPart request
            bool useSigV4 = initiateResponse.ServerSideEncryptionMethod == ServerSideEncryptionMethod.AWSKMS || 
                            initiateResponse.ServerSideEncryptionMethod == ServerSideEncryptionMethod.AWSKMSDSSE;
            if (useSigV4)
                ((IAmazonWebServiceRequest)uploadPartRequest).SignatureVersion = SignatureVersion.SigV4;

            return uploadPartRequest;
        }

        private UploadPartRequest ConstructUploadPartRequestForNonSeekableStream(
            Stream inputStream, 
            int partNumber, 
            long partSize, 
            bool isLastPart, 
            InitiateMultipartUploadResponse initiateResponse)
        {
            UploadPartRequest uploadPartRequest = ConstructGenericUploadPartRequest(initiateResponse);
            
            uploadPartRequest.InputStream = inputStream;
            uploadPartRequest.PartNumber = partNumber;
            uploadPartRequest.PartSize = partSize;
            uploadPartRequest.IsLastPart = isLastPart;

            var progressHandler = new ProgressHandler(
                (ulong) _s3Client.Config.ProgressUpdateInterval,
                _contentLength,
                uploadPartRequest.FilePath, 
                UploadPartProgressEventCallback
                );
                
            var eventStream = new EventStream(uploadPartRequest.InputStream);
            
            eventStream.OnRead += progressHandler.OnBytesRead;
            
            uploadPartRequest.InputStream = eventStream;
                
            ((IAmazonWebServiceRequest)uploadPartRequest).AddBeforeRequestHandler(RequestEventHandler);

            return uploadPartRequest;
        }

        private InitiateMultipartUploadRequest ConstructInitiateMultipartUploadRequest() => 
            ConstructInitiateMultipartUploadRequest(null);

        private InitiateMultipartUploadRequest ConstructInitiateMultipartUploadRequest(RequestEventHandler? requestEventHandler)
        {
            var initRequest = new InitiateMultipartUploadRequest
            {
                BucketName = _fileTransporterRequest.BucketName,
                Key = _fileTransporterRequest.Key,
                CannedACL = _fileTransporterRequest.CannedAcl,
                ContentType = DetermineContentType(),
                StorageClass = _fileTransporterRequest.StorageClass,
                ServerSideEncryptionMethod = _fileTransporterRequest.ServerSideEncryptionMethod,
                ServerSideEncryptionKeyManagementServiceKeyId = _fileTransporterRequest.ServerSideEncryptionKeyManagementServiceKeyId,
                ServerSideEncryptionCustomerMethod = _fileTransporterRequest.ServerSideEncryptionCustomerMethod,
                ServerSideEncryptionCustomerProvidedKey = _fileTransporterRequest.ServerSideEncryptionCustomerProvidedKey,
                ServerSideEncryptionCustomerProvidedKeyMD5 = _fileTransporterRequest.ServerSideEncryptionCustomerProvidedKeyMd5,
                TagSet = _fileTransporterRequest.TagSet,
                ChecksumAlgorithm = _fileTransporterRequest.ChecksumAlgorithm,
                ObjectLockLegalHoldStatus = _fileTransporterRequest.ObjectLockLegalHoldStatus,
                ObjectLockMode = _fileTransporterRequest.ObjectLockMode
            };

            if (_fileTransporterRequest.IsSetObjectLockRetainUntilDate())
                initRequest.ObjectLockRetainUntilDate = _fileTransporterRequest.ObjectLockRetainUntilDate;

            ((IAmazonWebServiceRequest)initRequest).AddBeforeRequestHandler(requestEventHandler ?? RequestEventHandler);

            if (_fileTransporterRequest.Metadata is { Count: > 0 })
                initRequest.Metadata.AddRange(_fileTransporterRequest.Metadata);
            if (_fileTransporterRequest.Headers is { Count: > 0 })
                initRequest.Headers.AddRange(_fileTransporterRequest.Headers);

            return initRequest;
        }

        private void UploadPartProgressEventCallback(object? sender, UploadProgressArgs e)
        {
            ulong transferredBytes = Interlocked.Add(
                ref _totalTransferredBytes, 
                e.IncrementTransferred - e.CompensationForRetry);

            var progressArgs = new UploadProgressArgs(
                e.IncrementTransferred, 
                transferredBytes, 
                _contentLength,
                e.CompensationForRetry, 
                _fileTransporterRequest.FilePath);
            _fileTransporterRequest.OnRaiseProgressEvent(progressArgs);
        }
    }

    internal class ProgressHandler
    {
        private UploadProgressArgs? _lastProgressArgs;
        private readonly EventHandler<UploadProgressArgs> _callback;
        private readonly ulong? _contentLength;
        private ulong _totalBytesRead;
        private ulong _totalIncrementTransferred;
        private readonly ulong _progressUpdateInterval;
        private string? _filePath;

        public ProgressHandler(
            ulong progressUpdateInterval,
            ulong? contentLength,
            string? filePath, 
            EventHandler<UploadProgressArgs> callback
            )
        {
            ArgumentNullException.ThrowIfNull(callback);
            
            _progressUpdateInterval = progressUpdateInterval;
            _contentLength = contentLength;
            _filePath = filePath;
            _callback = callback;
        }

        private void OnTransferProgress(object? sender, UploadProgressArgs e)
        {
            ulong compensationForRetry = 0U;

            if (_lastProgressArgs != null)
            {
                if (_lastProgressArgs.TransferredBytes >= e.TransferredBytes)
                {
                    // The request was retried
                    compensationForRetry = (ulong) _lastProgressArgs.TransferredBytes;
                }
            }

            var progressArgs = new UploadProgressArgs(e, compensationForRetry);
            
            _callback(this, progressArgs);

            _lastProgressArgs = e;
        }

        public void OnBytesRead(object? sender, StreamBytesReadEventArgs args)
        {
            if (_callback == null)
                return;

            var bytesRead = args.BytesRead;
            
            // Invoke the progress callback only if bytes read > 0
            if (bytesRead > 0)
            {
                _totalBytesRead += bytesRead;
                _totalIncrementTransferred += bytesRead;

                if (_totalIncrementTransferred >= _progressUpdateInterval ||
                    _totalBytesRead == _contentLength)
                {
                    var uploadProgressArgs = new UploadProgressArgs(
                        _totalIncrementTransferred,
                        _totalBytesRead,
                        _contentLength,
                        0,
                        _filePath
                    );
                    
                    AWSSDKUtils.InvokeInBackground(
                        OnTransferProgress,
                        uploadProgressArgs,
                        sender);
                    
                    _totalIncrementTransferred = 0;
                }
            }
        }
    }
}
