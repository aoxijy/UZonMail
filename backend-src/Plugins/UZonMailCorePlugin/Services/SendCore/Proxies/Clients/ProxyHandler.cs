﻿using log4net;
using MailKit.Net.Proxy;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using UZonMail.Core.Services.SendCore.Proxies.ProxyTesters;
using UZonMail.DB.SQL.Core.Settings;

namespace UZonMail.Core.Services.SendCore.Proxies.Clients
{
    /// <summary>
    /// 处理单个代理
    /// 使用 DI 进行使用
    /// </summary>
    /// <param name="url"></param>
    public class ProxyHandler : IProxyHandler
    {
        protected ProxyHandler() { }

        private readonly List<IProxyHealthChecker> _iPQueries;

        /// <summary>
        /// 参数从 DI 注入
        /// </summary>
        /// <param name="iPQueries"></param>
        public ProxyHandler(IEnumerable<IProxyHealthChecker> iPQueries)
        {
            _iPQueries = [.. iPQueries];
        }

        private static readonly ILog _logger = LogManager.GetLogger(typeof(ProxyHandler));

        public Proxy ProxyInfo { get; private set; }

        #region 协议相关
        /// <summary>
        /// 协议
        /// </summary>
        public string Schema { get; set; }

        /// <summary>
        /// 地址
        /// </summary>
        public string Host { get; set; }

        /// <summary>
        /// 端口
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// 用户名
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// 密码
        /// </summary>
        public string Password { get; set; } = string.Empty;
        #endregion

        #region 是否可用
        private bool _isHealthy = false;
        /// <summary>
        /// 是否 ping 通
        /// </summary>
        /// <returns></returns>
        public virtual bool IsEnable()
        {
            return ProxyInfo.IsActive && _isHealthy;
        }

        /// <summary>
        /// 标记为不健康，只有下一次 ping 通后才会恢复
        /// </summary>
        public virtual void MarkHealthless()
        {
            _isHealthy = false;
        }

        /// <summary>
        /// Handler 的 Id
        /// </summary>
        public string Id => ProxyInfo.Id > 0 ? ProxyInfo.Id.ToString() : ProxyInfo.ObjectId;

        /// <summary>
        /// 是否是动态代理: 当过期时间小于 30 分钟时，判断为动态代理
        /// 动态代理当检测不到时，就会自动停止检测
        /// </summary>
        public bool IsDynamic => _expireDate - DateTime.UtcNow < TimeSpan.FromMinutes(30);

        /// <summary>
        /// 过期时间
        /// </summary>
        private DateTime _expireDate = DateTime.MaxValue;
        private ProxyZoneType _testerType = ProxyZoneType.Default;
        /// <summary>
        /// 设置代理健康检测类型
        /// </summary>
        /// <param name="testerType"></param>
        private void SetProxyTesterType(ProxyZoneType testerType)
        {
            if (testerType.HasFlag(ProxyZoneType.Default))
            {
                return;
            }
            _testerType = testerType;
        }

        public virtual async Task<bool> HealthCheck()
        {
            var validIpQueries = _iPQueries.Where(x => x.Enable)
                .Where(x => x.ProxyZoneType.HasFlag(_testerType))
                .OrderBy(x => x.Order).ToList();
            if (validIpQueries.Count == 0)
            {
                _logger.Error("没有可用的有效代理检测接口, 代理将变得不稳定,请联系开发者解决");
                // 使用过期日期进行判断
                if (!_isHealthy) return false;
                _isHealthy = _expireDate < DateTime.UtcNow;
                return false;
            }

            // 开始检测            
            foreach (var ipQuery in validIpQueries)
            {
                var ipResult = await ipQuery.GetIP(ProxyInfo.Url);
                if (ipResult.Ok)
                {
                    // 网络通即说明使用了代理
                    // _isHealthy = Host.Equals(ipResult.Data);

                    _isHealthy = true;
                    _logger.Debug($"代理 {Id} 检测结果: {_isHealthy}");
                    return _isHealthy;
                }
            }

            _isHealthy = false;
            _logger.Debug($"代理 {Id} 检测失败");
            return false;
        }

        private int _healthCheckCount = 2;
        private Timer? _timer;
        /// <summary>
        /// 自动检测
        /// 每隔 1 分钟自动检测一次
        /// 若是动态代理，最多检测 2 次
        /// </summary>
        protected virtual void AutoHealthCheck()
        {
            if (_timer != null) return;
            _timer = new Timer(async _ =>
            {
                // 动态代理，检测不到就停止检测
                if (IsDynamic && !_isHealthy && _healthCheckCount < 0)
                {
                    _timer?.Dispose();
                    _timer = null;
                    return;
                }

                _isHealthy = await HealthCheck();
                _healthCheckCount--;
            }, null, 0, 1000 * 20); // 30s 检测一次
        }
        #endregion

        #region 使用历史记录
        private int _maxUsedCountPerDomain = -1;
        private readonly ConcurrentDictionary<string, int> _usageCounter = new();
        /// <summary>
        /// 记录使用信息
        /// </summary>
        /// <param name="email"></param>
        protected virtual void RecordUsage(string email)
        {
            var domain = email.Split('@').Last();

            if (_usageCounter.TryGetValue(domain, out var count))
            {
                _usageCounter[domain] = count + 1;
            }
            else
            {
                _usageCounter.TryAdd(domain, 1);
            }
        }
        #endregion

        #region 用户信息
        public long UserId { get; private set; } = 0;
        #endregion

        /// <summary>
        /// 与字符串的转换
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return $"{Schema}://{Username}:{Password}@{Host}:{Port}";
        }

        /// <summary>
        /// 是否匹配
        /// </summary>
        /// <param name="email"></param>
        /// <returns></returns>
        public virtual bool IsMatch(string email)
        {
            // 为空时，默认全部匹配
            if (string.IsNullOrEmpty(ProxyInfo.MatchRegex)) return true;

            // 规则不匹配时，返回 false
            if (!Regex.IsMatch(email, ProxyInfo.MatchRegex)) return false;

            // 未设置限制时，默认全部匹配
            if (_maxUsedCountPerDomain < 0) return true;

            // 判断使用次数是否超限
            var domain = email.Split('@').Last();
            if (_usageCounter.TryGetValue(domain, out var usedCount))
            {
                if (usedCount >= _maxUsedCountPerDomain)
                    return false;
            }

            return true;
        }

        private ProxyClientAdapter _proxyClientAdapter;

        /// <summary>
        /// 生成代理客户端
        /// </summary>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual async Task<ProxyClientAdapter?> GetProxyClientAsync(IServiceProvider serviceProvider, string email)
        {
            // 登记使用
            RecordUsage(email);

            if (_proxyClientAdapter != null) return _proxyClientAdapter;

            IProxyClient _proxyClient;
            NetworkCredential networkCredential = new(Username, Password);
            switch (Schema.ToLower())
            {
                case "socks5":
                    _proxyClient = new Socks5Client(Host, Port, networkCredential);
                    break;
                case "http":
                    _proxyClient = new HttpProxyClient(Host, Port, networkCredential);
                    break;
                case "https":
                    _proxyClient = new HttpsProxyClient(Host, Port, networkCredential);
                    break;
                case "socks4":
                    _proxyClient = new Socks4Client(Host, Port, networkCredential);
                    break;
                case "socks4a":
                    _proxyClient = new Socks4aClient(Host, Port, networkCredential);
                    break;
                default:
                    _logger.Error($"不支持的代理协议: {Schema}");
                    return null;
            }
            _proxyClientAdapter = new ProxyClientAdapter(this, _proxyClient);
            return _proxyClientAdapter;
        }

        /// <summary>
        /// 更新代理，并更新过期时间
        /// </summary>
        /// <param name="proxy"></param>
        /// <param name="expireSeconds">单位秒</param>
        public virtual void Update(Proxy proxy, ProxyZoneType proxyZoneType = ProxyZoneType.Default, int expireSeconds = int.MaxValue, int maxUsedCountPerDomain = -1, long userId = 0)
        {
            // 更新代理数据
            ProxyInfo = proxy;
            SetProxyTesterType(proxyZoneType);
            _expireDate = DateTime.UtcNow.AddSeconds(expireSeconds);
            _maxUsedCountPerDomain = maxUsedCountPerDomain;
            if (userId > 0) UserId = userId;

            // 将字符串转换为代理
            Uri uri = new(proxy.Url);
            Host = uri.Host;
            Port = uri.Port;
            Schema = uri.Scheme;

            var userInfos = uri.UserInfo.Split(':');
            if (userInfos.Length > 0)
            {
                Username = userInfos[0];
            }
            if (userInfos.Length > 1)
            {
                Password = userInfos[1];
            }

            // 强制检测，可能拖慢启动速度
            // HealthCheck().Wait();

            AutoHealthCheck();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public virtual void DisposeHandler()
        {
            return;
        }

        #region 静态方法
        /// <summary>
        /// 尝试解析代理字符串
        /// </summary>
        /// <param name="proxyString"></param>
        /// <param name="proxyInfo"></param>
        /// <returns></returns>
        public static bool CanParse(string proxyString)
        {
            return Uri.TryCreate(proxyString, UriKind.RelativeOrAbsolute, out _);
        }
        #endregion
    }
}
