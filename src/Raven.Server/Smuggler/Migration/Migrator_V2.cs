﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Raven.Client.Documents.Smuggler;
using Raven.Server.Documents;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents;
using Raven.Server.Smuggler.Documents.Data;
using Sparrow.Json;
using DatabaseSmuggler = Raven.Server.Smuggler.Documents.DatabaseSmuggler;

namespace Raven.Server.Smuggler.Migration
{
    public class Migrator_V2 : AbstractLegacyMigrator
    {
        private const int AttachmentsPageSize = 32;

        public Migrator_V2(DocumentDatabase database, MigratorOptions options) : base(database, options)
        {
        }

        public override async Task Execute()
        {
            var state = GetLastMigrationState();

            var migratedDocumentsOrAttachments = false;
            if (OperateOnTypes.HasFlag(DatabaseItemType.Documents))
            {
                await MigrateDocuments(state?.LastDocsEtag ?? LastEtagsInfo.EtagEmpty);
                migratedDocumentsOrAttachments = true;
            }

            if (OperateOnTypes.HasFlag(DatabaseItemType.LegacyAttachments))
            {
                await MigrateAttachments(state?.LastAttachmentsEtag ?? LastEtagsInfo.EtagEmpty);
                migratedDocumentsOrAttachments = true;
            }

            if (migratedDocumentsOrAttachments)
            {
                Result.Documents.Processed = true;
                OnProgress.Invoke(Result.Progress);
                await SaveLastOperationState(GenerateLastEtagsInfo());
            }

            if (OperateOnTypes.HasFlag(DatabaseItemType.Indexes))
                await MigrateIndexes();

            DatabaseSmuggler.EnsureProcessed(Result);
        }

        private async Task MigrateDocuments(string lastEtag)
        {
            var url = $"{ServerUrl}/databases/{DatabaseName}/streams/docs?etag={lastEtag}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancelToken.Token);
            if (response.IsSuccessStatusCode == false)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Failed to export documents from server: {ServerUrl}, " +
                                                    $"status code: {response.StatusCode}, " +
                                                    $"error: {responseString}");
            }

            using (var responseStream = await response.Content.ReadAsStreamAsync())
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var source = new StreamSource(responseStream, context, Database))
            {
                var destination = new DatabaseDestination(Database);
                var options = new DatabaseSmugglerOptionsServerSide
                {
                    ReadLegacyEtag = true
                };
                var smuggler = new DatabaseSmuggler(Database, source, destination, Database.Time, options, Result, OnProgress, CancelToken.Token);

                // since we will be migrating indexes as separate task don't ensureStepsProcessed at this point
                smuggler.Execute(ensureStepsProcessed: false);
            }
        }

        private async Task MigrateAttachments(string lastEtag)
        {
            var destination = new DatabaseDestination(Database);

            using (Database.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext transactionOperationContext))
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var documentActions = destination.Documents())
            {
                var sp = Stopwatch.StartNew();

                while (true)
                {
                    var attatchmentsArray = await GetAttachmentsList(lastEtag, transactionOperationContext);
                    if (attatchmentsArray.Length == 0)
                    {
                        var count = Result.Documents.Attachments.ReadCount;
                        if (count > 0)
                        {
                            var message = $"Read {count:#,#;;0} legacy attachment{(count > 1 ? "s" : string.Empty)}.";
                            Result.AddInfo(message);
                            OnProgress.Invoke(Result.Progress);
                        }

                        return;
                    }

                    foreach (var attachmentObject in attatchmentsArray)
                    {
                        var blittable = attachmentObject as BlittableJsonReaderObject;
                        if (blittable == null)
                            throw new InvalidDataException("attchmentObject isn't a BlittableJsonReaderObject");

                        if (blittable.TryGet("Key", out string key) == false)
                            throw new InvalidDataException("Key doesn't exist");

                        if (blittable.TryGet("Metadata", out BlittableJsonReaderObject metadata) == false)
                            throw new InvalidDataException("Metadata doesn't exist");

                        var dataStream = await GetAttachmentStream(key);
                        WriteDocumentWithAttachment(documentActions, context, dataStream, key, metadata);

                        Result.Documents.ReadCount++;
                        if (Result.Documents.Attachments.ReadCount % 50 == 0 || sp.ElapsedMilliseconds > 3000)
                        {
                            var message = $"Read {Result.Documents.Attachments.ReadCount:#,#;;0} legacy attachments.";
                            Result.AddInfo(message);
                            OnProgress.Invoke(Result.Progress);
                            sp.Restart();
                        }
                    }

                    var lastAttachment = attatchmentsArray.Last() as BlittableJsonReaderObject;
                    Debug.Assert(lastAttachment != null, "lastAttachment != null");
                    if (lastAttachment.TryGet("Etag", out string etag))
                        lastEtag = Result.LegacyLastAttachmentEtag = etag;
                }
            }
        }

        private async Task<BlittableJsonReaderArray> GetAttachmentsList(string lastEtag, TransactionOperationContext context)
        {
            var url = $"{ServerUrl}/databases/{DatabaseName}/static?pageSize={AttachmentsPageSize}&etag={lastEtag}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await HttpClient.SendAsync(request, CancelToken.Token);
            if (response.IsSuccessStatusCode == false)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Failed to get attachments list from server: {ServerUrl}, " +
                                                    $"status code: {response.StatusCode}, " +
                                                    $"error: {responseString}");
            }

            using (var responseStream = await response.Content.ReadAsStreamAsync())
            using (var attachmentsListStream = new ArrayStream(responseStream, "Attachments"))
            {
                var attachmentsList = await context.ReadForMemoryAsync(attachmentsListStream, "attachments-list");
                if (attachmentsList.TryGet("Attachments", out BlittableJsonReaderArray attachments) == false)
                    throw new InvalidDataException("Response is invalid");

                return attachments;
            }
        }

        private async Task<Stream> GetAttachmentStream(string attachmentKey)
        {
            var url = $"{ServerUrl}/databases/{DatabaseName}/static/{attachmentKey}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancelToken.Token);
            if (response.IsSuccessStatusCode == false)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Failed to get attachment, key: {attachmentKey}, from server: {ServerUrl}, " +
                                                    $"status code: {response.StatusCode}, " +
                                                    $"error: {responseString}");
            }

            return await response.Content.ReadAsStreamAsync();
        }

        private async Task MigrateIndexes()
        {
            var url = $"{ServerUrl}/databases/{DatabaseName}/indexes";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            var response = await HttpClient.SendAsync(request, CancelToken.Token);
            if (response.IsSuccessStatusCode == false)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Failed to export indexes from server: {ServerUrl}, " +
                                                    $"status code: {response.StatusCode}, " +
                                                    $"error: {responseString}");
            }

            using (var responseStream = await response.Content.ReadAsStreamAsync())
            using (var indexesStream = new ArrayStream(responseStream, "Indexes")) // indexes endpoint returns an array
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var source = new StreamSource(indexesStream, context, Database))
            {
                var destination = new DatabaseDestination(Database);
                var options = new DatabaseSmugglerOptionsServerSide
                {
                    RemoveAnalyzers = RemoveAnalyzers,
                };
                var smuggler = new DatabaseSmuggler(Database, source, destination, Database.Time, options, Result, OnProgress, CancelToken.Token);

                smuggler.Execute();
            }
        }

        private class ArrayStream : Stream
        {
            private Stream _baseStream;
            private readonly long _length;
            private long _position;

            private readonly MemoryStream _beginningStream;

            private readonly MemoryStream _endingStream =
                new MemoryStream(Encoding.UTF8.GetBytes("}"));

            public ArrayStream(Stream baseStream, string propertyName)
            {
                if (baseStream == null)
                    throw new ArgumentNullException(nameof(baseStream));
                if (baseStream.CanRead == false)
                    throw new ArgumentException("can't read base stream");
                if (baseStream.CanSeek == false)
                    throw new ArgumentException("can't seek in base stream");

                _beginningStream = new MemoryStream(Encoding.UTF8.GetBytes($"{{ \"{propertyName}\" : "));
                _baseStream = baseStream;
                _length = _beginningStream.Length + baseStream.Length + _endingStream.Length;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                CheckDisposed();

                var remaining = _length - _position;
                if (remaining <= 0)
                    return 0;

                if (remaining < count)
                    count = (int)remaining;

                int read;
                if (_beginningStream.Position < _beginningStream.Length)
                {
                    read = _beginningStream.Read(buffer, offset, count);
                }
                else
                {
                    read = _baseStream.Read(buffer, offset, count);
                    if (read == 0)
                    {
                        read = _endingStream.Read(buffer, offset, count);
                    }
                }

                _position += read;
                return read;
            }

            private void CheckDisposed()
            {
                if (_baseStream == null)
                    throw new ObjectDisposedException(GetType().Name);
            }

            public override long Length
            {
                get
                {
                    CheckDisposed();
                    return _length;
                }
            }

            public override bool CanRead
            {
                get
                {
                    CheckDisposed();
                    return true;
                }
            }

            public override bool CanWrite
            {
                get
                {
                    CheckDisposed();
                    return false;
                }
            }

            public override bool CanSeek
            {
                get
                {
                    CheckDisposed();
                    return false;
                }
            }

            public override long Position
            {
                get
                {
                    CheckDisposed();
                    return _position;
                }
                set => throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Flush()
            {
                CheckDisposed();
                _baseStream.Flush();
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing == false)
                    return;

                // the caller is responsible for disposing the base stream
                _baseStream = null;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }
        }
    }
}