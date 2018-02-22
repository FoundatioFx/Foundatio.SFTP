﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Foundatio.Storage {
    public class SshNetFileStorage : IFileStorage {
        private readonly ConnectionInfo _connectionInfo;
        private readonly ISerializer _serializer;
        protected readonly ILogger _logger;

        public SshNetFileStorage(SshNetFileStorageOptions options) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _connectionInfo = CreateConnectionInfo(options);
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger<SshNetFileStorage>.Instance;
        }

        public SshNetFileStorage(Builder<SshNetFileStorageOptionsBuilder, SshNetFileStorageOptions> config)
            : this(config(new SshNetFileStorageOptionsBuilder()).Build()) { }
        
        ISerializer IHaveSerializer.Serializer => _serializer;

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            try {
                using (var client = new SftpClient(_connectionInfo)) {
                    client.Connect();

                    var stream = new MemoryStream();
                    await Task.Factory.FromAsync(client.BeginDownloadFile(NormalizePath(path), stream, null, null), client.EndDownloadFile).AnyContext();
                    return stream;
                }
            } catch (SftpPathNotFoundException ex) {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace(ex, "Error trying to get file stream: {Path}", path);
                
                return null;
            }
        }

        public Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            try {
                using (var client = new SftpClient(_connectionInfo)) {
                    client.Connect();

                    var file = client.Get(NormalizePath(path));
                    return Task.FromResult(new FileSpec {
                        Path = file.FullName,
                        Created = file.LastWriteTimeUtc,
                        Modified = file.LastWriteTimeUtc,
                        Size = file.Length
                    });
                }
            } catch (SftpPathNotFoundException ex) {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace(ex, "Error trying to getting file info: {Path}", path);
                
                return Task.FromResult<FileSpec>(null);
            }
        }

        public Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            using (var client = new SftpClient(_connectionInfo)) {
                client.Connect();
                return Task.FromResult(client.Exists(NormalizePath(path)));
            }
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using (var client = new SftpClient(_connectionInfo)) {
                client.Connect();
                await Task.Factory.FromAsync(client.BeginUploadFile(stream, NormalizePath(path), null, null), client.EndUploadFile).AnyContext();
            }
            
            return true;
        }

        public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            using (var client = new SftpClient(_connectionInfo)) {
                client.Connect();
                client.RenameFile(NormalizePath(path), NormalizePath(newPath));
            }

            return Task.FromResult(true);
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            using (var stream = await GetFileStreamAsync(path, cancellationToken).AnyContext()) {
                if (stream == null)
                    return false;

                return await SaveFileAsync(targetPath, stream, cancellationToken).AnyContext();
            }
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default(CancellationToken)) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            try {
                using (var client = new SftpClient(_connectionInfo)) {
                    client.Connect();
                    client.DeleteFile(NormalizePath(path));
                }
            } catch (SftpPathNotFoundException ex) {
                _logger.LogDebug(ex, "Error trying to delete file: {Path}.", path);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public async Task DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default(CancellationToken)) {
            var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
            // TODO: We could batch this, but we should ensure the batch isn't thousands of files.
            foreach (var file in files)
                await DeleteFileAsync(file.Path).AnyContext();
        }

        public async Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (limit.HasValue && limit.Value <= 0)
                return new List<FileSpec>();

            var criteria = GetRequestCriteria(NormalizePath(searchPattern));
            
            var list = new List<FileSpec>();
            using (var client = new SftpClient(_connectionInfo)) {
                client.Connect();

                var files = await Task.Factory.FromAsync(client.BeginListDirectory(criteria.Prefix, null, null), client.EndListDirectory).AnyContext();
                foreach (var file in files) {
                    if (!file.IsRegularFile || !criteria.Pattern.IsMatch(file.Name))
                        continue;
                     
                    list.Add(new FileSpec {
                        Path = file.FullName,
                        Created = file.LastWriteTimeUtc,
                        Modified = file.LastWriteTimeUtc,
                        Size = file.Length
                    });
                }
            }
            
            if (skip.HasValue)
                list = list.Skip(skip.Value).ToList();
            
            if (limit.HasValue)
                list = list.Take(limit.Value).ToList();

            return list;
        }
        
        protected virtual ConnectionInfo CreateConnectionInfo(SshNetFileStorageOptions options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentNullException(nameof(options.ConnectionString));

            if (!Uri.TryCreate(options.ConnectionString, UriKind.Absolute, out var uri) || String.IsNullOrEmpty(uri?.UserInfo))
                throw new ArgumentException("Unable to parse connection string uri", nameof(options.ConnectionString));

            var userParts = uri.UserInfo.Split(new [] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            string username = userParts.First();
            string password = userParts.Length > 0 ? userParts[1] : null;
            int port = uri.Port > 0 ? uri.Port : 22;

            var authenticationMethods = new List<AuthenticationMethod>();
            if (!String.IsNullOrEmpty(password))
                authenticationMethods.Add(new PasswordAuthenticationMethod(username, password));
            
            if (options.PrivateKey != null)
                authenticationMethods.Add(new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(options.PrivateKey, options.PrivateKeyPassPhrase)));
            
            if (authenticationMethods.Count == 0)
                authenticationMethods.Add(new NoneAuthenticationMethod(username));

            if (!String.IsNullOrEmpty(options.Proxy)) {
                if (!Uri.TryCreate(options.Proxy, UriKind.Absolute, out var proxyUri) || String.IsNullOrEmpty(proxyUri?.UserInfo))
                    throw new ArgumentException("Unable to parse proxy uri", nameof(options.Proxy));
                
                var proxyParts = proxyUri.UserInfo.Split(new [] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                string proxyUsername = proxyParts.First();
                string proxyPassword = proxyParts.Length > 0 ? proxyParts[1] : null;
                
                var proxyType = options.ProxyType;
                if (proxyType == ProxyTypes.None && proxyUri.Scheme != null && proxyUri.Scheme.StartsWith("http"))
                    proxyType = ProxyTypes.Http;
                
                return new ConnectionInfo(uri.Host, port, username, proxyType, proxyUri.Host, proxyUri.Port, proxyUsername, proxyPassword, authenticationMethods.ToArray());
            }
            
            return new ConnectionInfo(uri.Host, port, username, authenticationMethods.ToArray());
        }
        
        private string NormalizePath(string path) {
            return path?.Replace('\\', '/');
        }
        
        private class SearchCriteria {
            public string Prefix { get; set; }
            public Regex Pattern { get; set; }
        }

        private SearchCriteria GetRequestCriteria(string searchPattern) {
            Regex patternRegex = null;
            searchPattern = searchPattern?.Replace('\\', '/');

            string prefix = searchPattern;
            int wildcardPos = searchPattern?.IndexOf('*') ?? -1;
            if (searchPattern != null && wildcardPos >= 0) {
                patternRegex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");
                int slashPos = searchPattern.LastIndexOf('/');
                prefix = slashPos >= 0 ? searchPattern.Substring(0, slashPos) : String.Empty;
            }

            return new SearchCriteria {
                Prefix = prefix ?? String.Empty,
                Pattern = patternRegex
            };
        }

        public void Dispose() {}
    }
}
