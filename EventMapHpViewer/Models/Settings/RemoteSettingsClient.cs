﻿using Codeplex.Data;
using Grabacr07.KanColleWrapper;
using Nekoxy;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EventMapHpViewer.Models.Settings
{
    class RemoteSettingsClient
    {
        private HttpClient client;

        private readonly ConcurrentDictionary<string, DateTimeOffset> lastModified;

        private readonly ConcurrentDictionary<string, object> caches;

        private TimeSpan cacheTtl;

        private bool updating;

        private static readonly object errorObject = new object();

        public bool IsCacheError { get; set; } = true;

#if DEBUG
        public RemoteSettingsClient() : this(TimeSpan.FromSeconds(10)) { }
#else
        public RemoteSettingsClient() : this(TimeSpan.FromHours(1)) { }
#endif

        public RemoteSettingsClient(TimeSpan cacheTtl)
        {
            this.lastModified = new ConcurrentDictionary<string, DateTimeOffset>();
            this.caches = new ConcurrentDictionary<string, object>();
            this.cacheTtl = cacheTtl;
        }

        /// <summary>
        /// 艦これ戦術データ・リンクから設定情報を取得する。
        /// 取得できなかった場合は null を返す。
        /// </summary>
        /// <param name="mapId"></param>
        /// <param name="rank"></param>
        /// <returns></returns>
        public async Task<T> GetSettings<T>(string url)
            where T : class
        {
            DateTimeOffset lm;
            lock (this.lastModified)
            {
                while (this.updating)
                {
                    Thread.Sleep(100);
                }

                lm = this.lastModified.GetOrAdd(url, DateTimeOffset.MinValue);

                if (DateTimeOffset.Now - lm < this.cacheTtl)
                {
                    if (this.caches.TryGetValue(url, out var value))
                    {
                        if (value is T)
                            return (T)value;
                        else if (value == errorObject)
                            return null;
                    }
                }
                this.updating = true;
            }

            if (this.client == null)
            {
                this.client = new HttpClient(GetProxyConfiguredHandler());
                this.client.DefaultRequestHeaders
                    .TryAddWithoutValidation("User-Agent", $"{MapHpViewer.title}/{MapHpViewer.version}");
            }
            try
            {
                Debug.WriteLine($"MapHP - GET: {url}");
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    // 200 じゃなかった
                    this.CacheError(url, lm);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                T parsed = DynamicJson.Parse(json);
                this.lastModified.TryUpdate(url, DateTimeOffset.Now, lm);
                this.caches.AddOrUpdate(url, parsed, (_, __) => parsed);
                return parsed;
            }
            catch (HttpRequestException)
            {
                // HTTP リクエストに失敗した
                this.CacheError(url, lm);
                return null;
            }
            catch
            {
                // 不正な JSON 等
                this.CacheError(url, lm);
                return null;
            }
            finally
            {
                this.updating = false;
            }
        }

        private void CacheError(string url, DateTimeOffset lastModified)
        {
            if (!this.IsCacheError)
                return;
            this.lastModified.TryUpdate(url, DateTimeOffset.Now, lastModified);
            this.caches.AddOrUpdate(url, errorObject, (_, __) => errorObject);
        }

        public void CloseConnection()
        {
            this.client?.Dispose();
            this.client = null;
        }

        /// <summary>
        /// 本体のプロキシ設定を組み込んだHttpClientHandlerを返す。
        /// </summary>
        /// <returns></returns>
        private static HttpClientHandler GetProxyConfiguredHandler()
        {
            switch (HttpProxy.UpstreamProxyConfig.Type)
            {
                case ProxyConfigType.DirectAccess:
                    return new HttpClientHandler
                    {
                        UseProxy = false
                    };
                case ProxyConfigType.SpecificProxy:
                    var settings = KanColleClient.Current.Proxy.UpstreamProxySettings;
                    var host = settings.IsUseHttpProxyForAllProtocols ? settings.HttpHost : settings.HttpsHost;
                    var port = settings.IsUseHttpProxyForAllProtocols ? settings.HttpPort : settings.HttpsPort;
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        return new HttpClientHandler { UseProxy = false };
                    }
                    else
                    {
                        return new HttpClientHandler
                        {
                            UseProxy = true,
                            Proxy = new WebProxy($"{host}:{port}"),
                        };
                    }
                case ProxyConfigType.SystemProxy:
                    return new HttpClientHandler();
                default:
                    return new HttpClientHandler();
            }
        }

        public static string BuildBossSettingsUrl(string url, int id, int rank, int gaugeNum)
        {
            return BuildUrl(url, new Dictionary<string, string>
            {
                { "version", $"{MapHpViewer.version}" },
                { "mapId", id.ToString() },
                { "rank", rank.ToString() },
                { "gaugeNum", gaugeNum.ToString() },
            });
        }

        public static string BuildUrl(string url, IDictionary<string, string> placeHolders)
        {
            if (placeHolders == null)
                return url;
            foreach(var placeHolder in placeHolders)
            {
                var regex = new Regex($"{{{placeHolder.Key}}}", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                url = regex.Replace(url, placeHolder.Value);
            }
            return url;
        }
    }
}
