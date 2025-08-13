﻿using log4net;
using MailKit.Net.Proxy;
using MailKit.Security;
using Microsoft.Identity.Client;
using MimeKit;
using Newtonsoft.Json.Linq;
using System.Net;
using UZonMail.Core.Services.Config;
using UZonMail.Core.Services.Encrypt;
using UZonMail.DB.Extensions;
using UZonMail.DB.SQL;
using UZonMail.Utils.Extensions;
using UZonMail.Utils.Http.Request;
using UZonMail.Utils.Json;

namespace UZonMail.Core.Services.SendCore.Sender.MsGraph
{
    /// <summary>
    /// 参考： https://learn.microsoft.com/en-us/graph/sdks/choose-authentication-providers?tabs=csharp#client-credentials-provider
    /// </summary>
    /// <param name="email"></param>
    /// <param name="cooldownMilliseconds"></param>
    public class MsGraphClient(EncryptService encryptService, IConfiguration configuration, DebugConfig debugConfig) : IEmailSendingClient
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(MsGraphClient));
        private static HttpClient _httpClient = new();

        private bool _isRefreshTokenChanged = false;
        private AuthenticationResult2? _authenticationResult;
        private AuthenticationResult2? AuthenticationResult
        {
            get => _authenticationResult;
            set
            {
                var newRefreshToken = value?.RefreshToken ?? string.Empty;
                var oldRefreshToken = _authenticationResult?.RefreshToken ?? string.Empty;
                _isRefreshTokenChanged = !string.IsNullOrEmpty(newRefreshToken) && newRefreshToken != oldRefreshToken;

                _authenticationResult = value;
            }
        }
        private MsGraphParamsResolver _msGraphParams;

        private string _authenticateInputMd5 = string.Empty;

        private string _email;
        private int _cooldownMilliseconds;
        public void SetParams(string email, int cooldownMilliseconds)
        {
            _email = email;
            _cooldownMilliseconds = cooldownMilliseconds;
        }

        public IProxyClient ProxyClient { get; set; }

        /// <summary>
        /// 验证邮箱是否正确
        /// 该接口不会保存新的 refreshToken 到数据库中, 若需要，请调用 AuthenticateAsync 的重载方法
        /// 外部尽量不要调用
        /// </summary>
        /// <param name="email"></param>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <returns></returns>
        /// <exception cref="AuthenticationException"></exception>
        public async Task AuthenticateAsync(string email, string username, string password)
        {
            // tempKey 保证数据修改后，会继续验证
            var inputMd5 = $"{email}-{username}-{password}".MD5();
            if (AuthenticationResult != null
                && AuthenticationResult.ExpireAt > DateTime.Now
                && _authenticateInputMd5 == inputMd5) return;
            _authenticateInputMd5 = inputMd5;


            // 解析用户名和密码
            _msGraphParams = new MsGraphParamsResolver(configuration);
            var msGraphParams = _msGraphParams;
            msGraphParams.SetGraphInfo(username, password);
            if (string.IsNullOrEmpty(msGraphParams.ClientId))
            {
                string message = "Outlook 邮箱没有配置用户名，无法进行 OAuth2 验证。";
                _logger.Warn(message);
                throw new AuthenticationException(message);
            }

            // 授权码流（Authorization Code Flow）形式
            if (msGraphParams.HasRefreshToken)
            {
                // 说明是 clientId, refreshToken 的形式
                AuthenticationResult = await GetAccessByRefreshToken(msGraphParams.ClientId, msGraphParams.ClientSecret, msGraphParams.RefreshToken!)
                    ?? throw new AuthenticationException("Outlook 邮箱的 refreshToken 无效或已过期，请检查配置。");
                return;
            }

            var tenantId = msGraphParams.TenantId;
            var clientId = msGraphParams.ClientId;

            // 客户端凭据流（Client Credentials Flow）形式
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId))
            {
                _logger.Warn("Outlook 邮箱的用户名格式不正确，应为 tenantId/clientId 的形式。");
                throw new AuthenticationException("Outlook 邮箱的用户名格式不正确，应为 tenantId/clientId 的形式。");
            }

            var authenticateResult = await GetConfidentialClientOAuth2CredentialsAsync(tenantId, clientId, msGraphParams.ClientSecret);
            AuthenticationResult = AuthenticationResult2.FromAuthenticationResult(authenticateResult);
            return;
        }

        /// <summary>
        /// 验证邮箱，并将 refreshToken 保存到数据库中
        /// 要求邮箱只能属于一个账号
        /// </summary>
        /// <param name="email"></param>
        /// <param name="username"></param>
        /// <param name="password">解密后的密码</param>
        /// <param name="db"></param>
        /// <returns></returns>
        public async Task AuthenticateAsync(string email, string username, string password, long userId, SqlContext db)
        {
            // 直接调用 AuthenticateAsync
            await AuthenticateAsync(email, username, password);

            // 判断是否采用 refreshToken 的方式发件，若是，则保存 refreshToken 到数据库中
            if (username.Contains('/')) return;

            // 加密 refreshToken
            if (string.IsNullOrEmpty(AuthenticationResult!.RefreshToken)) return;
            if (!_isRefreshTokenChanged) return;

            // 保存新的 refreshToken 到数据库中
            var encryptedPassword = encryptService.EncryptOutboxSecret(userId, AuthenticationResult.RefreshToken);
            await db.Outboxes.UpdateAsync(x => x.UserId == userId && x.Email == email, x => x.SetProperty(y => y.Password, encryptedPassword));
        }

        /// <summary>
        /// 正常情况下获取 accessToken
        /// </summary>
        /// <param name="protocol"></param>
        /// <param name="tenantId"></param>
        /// <param name="clientId"></param>
        /// <param name="clientSecret"></param>
        /// <returns></returns>
        [Obsolete("暂未完全测试")]
        private static async Task<AuthenticationResult> GetConfidentialClientOAuth2CredentialsAsync(string tenantId, string clientId, string clientSecret)
        {
            var loginUrl = "https://login.microsoftonline.com/";
            var confidentialClientApplication = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithAuthority($"{loginUrl}{tenantId}/v2.0")
                .WithClientSecret(clientSecret) // or .WithClientSecret (clientSecret)
                .Build();


            //var scopes = [
            //      // For IMAP and POP3, use the following scope
            //      "https://ps.outlook.com/.default"
            //  ];
            // For SMTP, use the following scope
            var scopes = new List<string>() { "https://graph.microsoft.com/.default" };
            return await confidentialClientApplication.AcquireTokenForClient(scopes).ExecuteAsync();
        }

        /// <summary>
        /// 刷新访问令牌
        /// </summary>
        /// <param name="clientId"></param>
        /// <param name="refreshToken"></param>
        /// <returns></returns>
        private static async Task<AuthenticationResult2?> GetAccessByRefreshToken(string clientId, string? clienSecret, string refreshToken)
        {
            var formContent = new Dictionary<string, string>()
            {
                { "client_id", clientId },
                { "refresh_token", refreshToken},
                { "grant_type", "refresh_token"},
                { "scope", "https://graph.microsoft.com/.default"}
            };
            if (!string.IsNullOrEmpty(clienSecret))
            {
                formContent.Add("client_secret", clienSecret);
            }

            var token_url = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
            var fluentHttpRequest = new FluentHttpRequest()
                .WithHttpClient(_httpClient)
                .WithMethod(HttpMethod.Post)
                .WithUrl(token_url)
                .WithFormContent(formContent);

            var response = await fluentHttpRequest.SendAsync();
            var responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                // 表示没有成功请求到授权
                _logger.Error($"{clientId} 请求授权失败: {responseContent}");
                // 获取错误信息
                var errorMessage = JObject.Parse(responseContent).SelectTokenOrDefault("error_description", "未知错误");
                throw new AuthenticationException(errorMessage);
            }
            var jsonResult = responseContent.JsonTo<AuthenticationResult2>();
            if (jsonResult == null)
            {
                _logger.Warn($"{clientId} 请求授权失败:{responseContent}");
                throw new AuthenticationException("返回结果非预期值");
            }

            // 判断是否有 SMTP.Send 权限
            if (!jsonResult.Scope.Contains("Mail.Send"))
                throw new AuthenticationException($"{clientId} 缺失 Mail.Send 权限");

            return jsonResult;
        }

        /// <summary>
        /// 开始发件
        /// 参考: https://learn.microsoft.com/en-us/graph/api/user-sendmail?view=graph-rest-1.0&tabs=http#request-3
        /// </summary>
        /// <param name="mimeMessage"></param>
        /// <returns></returns>
        public async Task<string> SendAsync(MimeMessage mimeMessage)
        {
            if (debugConfig.PreventSending)
            {
                return "调试模式中已阻止真实发件";
            }

            var encodedEmail = Uri.EscapeDataString(_email);

            var apiPath = _authenticationResult!.IsPersonalAccount ? "me" : $"users{encodedEmail}";
            var request = new MsGraphSendMailRequest()
                 .WithAccessToken(AuthenticationResult!.AccessToken)
                 .WithMimeMessage(mimeMessage)
                 .WithUrl($"https://graph.microsoft.com/v1.0/{apiPath}/sendMail")
                 .WithHttpClient(_httpClient);

            var response = await request.SendAsync();
            // 根据状态返回发送结果
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return string.Empty;
            }

            // 其它情况，表示发送失败
            _logger.Error($"发件箱 {_email} 错误。{response.ReasonPhrase}");
            var responseResult = await response.Content.ReadAsStringAsync();
            _logger.Error($"发件箱 {_email} 错误详情：{responseResult}");

            // 抛出异常
            throw new Exception($"发件箱 {_email} 错误：{response.ReasonPhrase}，详情：{responseResult}");
        }
    }
}
