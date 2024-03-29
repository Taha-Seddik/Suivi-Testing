﻿using Azure.Storage.Blobs;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using Azure.Storage.Blobs.Models;
using System;
using Microsoft.AspNetCore.Http;
using System.Drawing.Imaging;

namespace BlobStorage
{
    public class BlobStorageService : IBlobStorageService
    {
        private BlobStorageConfig _config;
        private ILogger _logger;
        private BlobServiceClient _blobServiceClient;
        private BlobContainerClient _containerClient;
        private readonly string FileNameMetaDataAttribut = "FileName";

        public BlobStorageService(IConfiguration configuration, ILogger<BlobStorageService> logger)
        {
            var blobStorageConfig = new BlobStorageConfig();
            configuration.GetSection(blobStorageConfig.ConfigId).Bind(blobStorageConfig);
            _config = blobStorageConfig;
            _logger = logger;
            _blobServiceClient = new BlobServiceClient(blobStorageConfig.ConnectionString);
        }


        public async Task<IFileDescriptor?> AddFileFromStream(Stream file, long size, string fileName, string? defaultfileId = null)
        {
            if (file == null) return null;
            string fileId = defaultfileId != null ? defaultfileId : Ulid.NewUlid().ToString();
            string contentType = BlobStorageUtils.GetContentType(fileName);
            var containerClient = await GetBlobContainerAsync();
            BlobClient blobClient = containerClient.GetBlobClient(fileId);
            // Blob MetaData
            IDictionary<string, string> metadata = new Dictionary<string, string>
            {
                { FileNameMetaDataAttribut, fileName},
            };
            // Blob ContentType
            BlobHttpHeaders blobHeaders = new BlobHttpHeaders()
            {
                ContentType = contentType,
            };
            file.Position = 0;
            await blobClient.UploadAsync(file, blobHeaders, metadata);
            return new FileDescriptor()
            {
                Id = fileId,
                FileName = fileName,
                Size = size,
                ContentType = contentType
            };
        }

        public async Task<IEnumerable<IFileDescriptor>?> AddFilesFromHTTPRequestAsync(IEnumerable<IFormFile> requestFiles)
        {
            List<IFileDescriptor> result = new List<IFileDescriptor>();
            foreach (var formFile in requestFiles)
            {
                if (formFile.Length > 0)
                {
                    var fileDescriptor = await AddFileFromStream(formFile.OpenReadStream(), formFile.Length, formFile.FileName);
                    if (fileDescriptor != null) result.Add(fileDescriptor);
                }
            }
            LogAllFiles();
            return (result.Count > 0) ? result : null;
        }

        public async Task<IFileDescriptor> GetBlobMetaDataAsync(string fileId)
        {
            //string fileId = fileRef.Value;
            var containerClient = await GetBlobContainerAsync();
            BlobClient blobClient = containerClient.GetBlobClient(fileId);
            if (await blobClient.ExistsAsync())
            {
                BlobProperties properties = await blobClient.GetPropertiesAsync();
                var fileName = properties.Metadata[FileNameMetaDataAttribut];
                return new FileDescriptor()
                {
                    Id = fileId,
                    FileName = fileName,
                    ContentType = properties.ContentType,
                    Size = properties.ContentLength
                };
            }
            else
            {
                throw new Exception("Blob doesn't exist");
            }
        }

        public async Task<BlobDownloadInfo?> GetFileStreamAsync(string fileId)
        {
            var containerClient = await GetBlobContainerAsync();
            BlobClient blobClient = containerClient.GetBlobClient(fileId);
            if (await blobClient.ExistsAsync())
            {
                return await blobClient.DownloadAsync();
            }
            else
            {
                return null;
            }
        }

        public async Task<Stream?> GetThumbnailStreamAsync(string fileId, bool fill, int? x, int? y)
        {
            var key = string.Format("{0}{1}_{2}_{3}", fileId, fill ? "_fill" : "", x, y);
            var containerClient = await GetBlobContainerAsync();
            BlobClient blobClient = containerClient.GetBlobClient(key);
            if (!await blobClient.ExistsAsync())
            {
                var sourceDownloadInfo = await GetFileStreamAsync(fileId);
                if (sourceDownloadInfo == null) // source not found
                {
                    throw new ArgumentException(string.Format("FileId: {0} not found", fileId));
                }
                // create new thumbnail and persist it
                var thumbBitmap = BlobStorageUtils.BuildThumbnailBitmap(sourceDownloadInfo, fill, x, y);
                if (thumbBitmap != null)
                {
                    MemoryStream memoryStream = new MemoryStream();
                    thumbBitmap.Save(memoryStream, ImageFormat.Jpeg);
                    await AddFileFromStream(memoryStream, memoryStream.Length, key, blobClient.Name);
                    memoryStream.Seek(0, SeekOrigin.Begin);
                    return memoryStream;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                // thumbnail already exists 
                var downloadedBlob = await blobClient.DownloadAsync();
                return downloadedBlob.Value.Content;
            }
        }

        private async void LogAllFiles()
        {
            Console.WriteLine("\t ############### All Files ###############");
            await foreach (BlobItem blobItem in _containerClient.GetBlobsAsync())
            {
                Console.WriteLine("\t" + blobItem.Name);
            }
            Console.WriteLine("\t ############### / All Files ###############");
        }

        private async Task<BlobContainerClient> GetBlobContainerAsync()
        {
            if (_containerClient == null)
            {
                try
                {
                    _logger.LogInformation("Try Creating storage container", _config.ContainerName);
                    _containerClient = await _blobServiceClient.CreateBlobContainerAsync(_config.ContainerName);
                    _logger.LogInformation("Container has been created");
                }
                catch (Exception)
                {
                    _containerClient = _blobServiceClient.GetBlobContainerClient(_config.ContainerName);
                    _logger.LogInformation("Container was found");
                }

            }
            return _containerClient;
        }

        public string GetFileNameMetaDataAttribut()
        {
            return FileNameMetaDataAttribut;
        }
    }
}
