﻿using UZonMail.Utils.Extensions;
using UZonMail.Core.Utils.Database;
using log4net;
using UZonMail.Core.Services.SendCore.Utils;
using UZonMail.DB.Managers.Cache;
using UZonMail.Core.Services.SendCore.Contexts;
using UZonMail.Core.Services.SendCore.Interfaces;
using UZonMail.DB.SQL.Core.EmailSending;
using UZonMail.DB.SQL.Core.Emails;

namespace UZonMail.Core.Services.SendCore.Outboxes
{
    /// <summary>
    /// 发件箱地址
    /// 该地址可能仅用于部分发件箱
    /// 也有可能是用于通用发件
    /// </summary>
    public class OutboxEmailAddress : EmailAddress, IWeight
    {
        private readonly static ILog _logger = LogManager.GetLogger(typeof(OutboxEmailAddress));

        #region 私有变量
        private readonly object _lock = new();

        // 发件箱数据
        private Outbox _outbox;

        /// <summary>
        /// 发送目录的 id
        /// </summary>
        private HashSet<SendingTargetId> _sendingTargetIds = [];

        /// <summary>
        /// 开始日期
        /// </summary>
        private DateTime _startDate = DateTime.UtcNow;
        #endregion

        #region 公开属性
        public OutboxEmailAddressType Type { get; private set; } = OutboxEmailAddressType.Specific;

        /// <summary>
        /// 用户 ID
        /// </summary>
        public long UserId => _outbox.UserId;

        /// <summary>
        /// 权重
        /// </summary>
        public int Weight { get; private set; }

        /// <summary>
        /// 授权用户名
        /// </summary>
        public string? SmtpAuthUserName
        {
            get { return string.IsNullOrEmpty(_outbox.UserName) ? _outbox.Email : _outbox.UserName; }
        }

        /// <summary>
        /// Outlook 的授权用户名
        /// 实际当成 clientId 在使用
        /// </summary>
        public string OutlookClientId => _outbox.UserName ?? string.Empty;

        //public OutboxAuthType AuthType => _outbox.AuthType;

        //public string ClientId => _outbox.ClientId ?? string.Empty;

        //public string TenantId => _outbox.TenantId ?? string.Empty;

        /// <summary>
        /// 授权密码或者 OAuth 的 secrete
        /// </summary>
        public string? AuthPassword { get; private set; }

        /// <summary>
        /// SMTP 服务器地址
        /// </summary>
        public string SmtpHost => _outbox.SmtpHost;

        /// <summary>
        /// SMTP 端口
        /// </summary>
        public int SmtpPort => _outbox.SmtpPort;

        /// <summary>
        /// 开启 SSL
        /// </summary>
        public bool EnableSSL => _outbox.EnableSSL;

        /// <summary>
        /// 单日最大发送数量
        /// 为 0 时表示不限制
        /// </summary>
        public int MaxSendCountPerDay => _outbox.MaxSendCountPerDay;

        /// <summary>
        /// 当天合计发件
        /// 成功失败都被计算在内
        /// </summary>
        public int SentTotalToday { get; private set; }

        /// <summary>
        /// 本次合计发件
        /// </summary>
        public int SentTotal { get; private set; }

        /// <summary>
        /// 递增发送数量
        /// 或跨越天数, 重置发送数量
        /// 对于发件箱来说，是单线程，因此不需要考虑并发问题
        /// </summary>
        public void IncreaseSentCount()
        {
            SentTotal++;

            // 重置每日发件量
            if (_startDate.Date != DateTime.UtcNow.Date)
            {
                _startDate = DateTime.UtcNow;
                SentTotalToday = 0;
            }
            else
            {
                SentTotalToday++;
            }
        }

        /// <summary>
        /// 代理 Id
        /// </summary>
        public long ProxyId => _outbox.ProxyId;

        /// <summary>
        /// 回复至邮箱
        /// </summary>
        public List<string> ReplyToEmails { get; set; } = [];

        /// <summary>
        /// 错误原因
        /// </summary>
        public string ErroredMessage { get; private set; } = "";

        /// <summary>
        /// 是否应释放
        /// </summary>
        public bool ShouldDispose { get; private set; } = false;

        /// <summary>
        /// 工作中
        /// 当没有发送目标后，working 为 false
        /// </summary>
        public bool Working => _sendingTargetIds.Count > 0;

        /// <summary>
        /// 是否可用
        /// </summary>
        public bool Enable
        {
            get => !ShouldDispose && !_isCooling && !_usingLock.IsLocked && Working;
        }
        #endregion

        #region 构造函数
        /// <summary>
        /// 生成发件地址
        /// </summary>
        /// <param name="outbox"></param>
        /// <param name="sendingGroupId"></param>
        /// <param name="smtpPasswordSecretKeys"></param>
        /// <param name="type"></param>
        /// <param name="sendingItemIds"></param>
        public OutboxEmailAddress(Outbox outbox, long sendingGroupId, List<string> smtpPasswordSecretKeys, OutboxEmailAddressType type, List<long> sendingItemIds = null)
        {
            _outbox = outbox;
            AuthPassword = outbox.Password.DeAES(smtpPasswordSecretKeys.First(), smtpPasswordSecretKeys.Last());
            Type = type;

            // 共享发件箱
            if (Type.HasFlag(OutboxEmailAddressType.Shared))
            {
                _sendingTargetIds.Add(new SendingTargetId(sendingGroupId));
            }

            if (sendingItemIds != null)
            {
                if (!Type.HasFlag(OutboxEmailAddressType.Specific))
                    throw new Exception("特定发件箱的 Type 必须包含 Specific");

                // 开始添加
                sendingItemIds?.ForEach(x => _sendingTargetIds.Add(new SendingTargetId(sendingGroupId, x)));
            }


            CreateDate = DateTime.UtcNow;
            Email = outbox.Email;
            Name = outbox.Name;
            Id = outbox.Id;

            ReplyToEmails = outbox.ReplyToEmails.SplitBySeparators().Distinct().ToList();
            SentTotalToday = outbox.SentTotalToday;
            Weight = outbox.Weight > 0 ? outbox.Weight : 1;
        }
        #endregion

        #region 更新发件箱
        /// <summary>
        /// 使用 OutboxEmailAddress 更新既有的发件地址
        /// 非并发操作
        /// </summary>
        /// <param name="data"></param>
        public void Update(OutboxEmailAddress data)
        {
            // 更新类型
            Type |= data.Type;
            Weight = data.Weight;
            ReplyToEmails = data.ReplyToEmails;

            // 更新关联的项
            foreach (var targetId in data._sendingTargetIds)
            {
                _sendingTargetIds.Add(targetId);
            }
        }
        #endregion

        #region 使用和冷却状态切换
        // 使用状态锁
        private readonly Locker _usingLock = new();

        // 标志
        private bool _isCooling = false;

        /// <summary>
        /// 锁定使用权
        /// </summary>
        /// <returns></returns>
        public bool LockUsing()
        {
            return _usingLock.Lock();
        }

        /// <summary>
        /// 释放使用权
        /// 需要在程序逻辑最后一刻才释放
        /// </summary>
        public void UnlockUsing()
        {
            _usingLock.Unlock();
        }

        /// <summary>
        /// 设置冷却
        /// 若设置失败，则返回 false
        /// </summary>
        /// <returns></returns>
        private void ChangeCoolingSate(bool cooling)
        {
            _isCooling = cooling;
        }

        private readonly Cooler _emailCooler = new();
        /// <summary>
        /// 进入冷却状态
        /// </summary>
        /// <param name="cooldownMilliseconds"></param>
        /// <param name="threadsManager"></param>
        public void StartCooling(long cooldownMilliseconds, SendingThreadsManager threadsManager)
        {
            ChangeCoolingSate(true);
            _emailCooler.StartCooling(cooldownMilliseconds, () =>
            {
                ChangeCoolingSate(false);
                threadsManager.StartSending(1);
                _logger.Debug($"发件箱 {Email} 退出冷却状态");
            });
        }
        #endregion

        #region 外部调用，改变内部状态

        /// <summary>
        /// 是否被禁用
        /// </summary>
        /// <returns></returns>
        public bool IsLimited()
        {
            return this.SentTotalToday >= this.MaxSendCountPerDay;
        }

        /// <summary>
        /// 是否包含指定的发件组
        /// </summary>
        /// <param name="sendingGroupId"></param>
        /// <returns></returns>
        public bool ContainsSendingGroup(long sendingGroupId)
        {
            return _sendingTargetIds.Select(x => x.SendingGroupId).Contains(sendingGroupId);
        }

        /// <summary>
        /// 获取发件组 id
        /// </summary>
        /// <returns></returns>
        public List<long> GetSendingGroupIds()
        {
            return _sendingTargetIds.Select(x => x.SendingGroupId).ToList();
        }

        /// <summary>
        /// 获取指定了发件箱的邮件
        /// </summary>
        /// <returns></returns>
        public List<long> GetSpecificSendingItemIds()
        {
            return _sendingTargetIds.Where(x => x.SendingGroupId > 0).Select(x => x.SendingItemId).ToList();
        }

        /// <summary>
        /// 移除指定的发件项
        /// </summary>
        /// <param name="sendingGroupId"></param>
        /// <param name="sendingItemId"></param>
        public void RemoveSepecificSendingItem(long sendingGroupId, long sendingItemId)
        {
            _sendingTargetIds.Remove(new SendingTargetId(sendingGroupId, sendingItemId));
        }

        /// <summary>
        /// 移除指定发送组
        /// </summary>
        /// <param name="sendingGroupId"></param>
        public void RemoveSendingGroup(long sendingGroupId)
        {
            _sendingTargetIds = _sendingTargetIds.Where(x => x.SendingGroupId != sendingGroupId).ToHashSet();
        }

        /// <summary>
        /// 标记应该释放
        /// </summary>
        /// <param name="erroredMessage"></param>
        public void MarkShouldDispose(string erroredMessage)
        {
            ErroredMessage = erroredMessage;
            ShouldDispose = true;
        }
        #endregion
    }
}
