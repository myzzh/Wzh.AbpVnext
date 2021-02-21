﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EasyAbp.Abp.WeChat;
using EasyAbp.Abp.WeChat.Common.Exceptions;
using EasyAbp.Abp.WeChat.MiniProgram;
using EasyAbp.Abp.WeChat.MiniProgram.Infrastructure;
using EasyAbp.Abp.WeChat.MiniProgram.Services.ACode;
using EasyAbp.Abp.WeChat.MiniProgram.Services.Login;
using EasyAbp.WeChatManagement.Common;
using EasyAbp.WeChatManagement.MiniPrograms;
using EasyAbp.WeChatManagement.MiniPrograms.Login;
using EasyAbp.WeChatManagement.MiniPrograms.Login.Dtos;
using EasyAbp.WeChatManagement.MiniPrograms.MiniPrograms;
using EasyAbp.WeChatManagement.MiniPrograms.MiniProgramUsers;
using EasyAbp.WeChatManagement.MiniPrograms.Settings;
using EasyAbp.WeChatManagement.MiniPrograms.UserInfos;
using IdentityModel;
using IdentityModel.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Caching;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;
using Volo.Abp.Json;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;
using Volo.Abp.Users;
using IdentityUser = Volo.Abp.Identity.IdentityUser;

namespace Wzh.AbpVnext.WeChatManagement.MiniPrograms.Login
{
    [RemoteService(IsEnabled = false)]
    [Dependency(ReplaceServices = true)]
    [ExposeServices(typeof(ILoginAppService))]
    public class WzhLoginAppService : MiniProgramsAppService, ILoginAppService, IWzhLoginAppService
    {
        protected virtual string BindPolicyName { get; set; }

        private readonly LoginService _loginService;
        private readonly ACodeService _aCodeService;
        private readonly SignatureChecker _signatureChecker;
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly IDataFilter _dataFilter;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IUserInfoRepository _userInfoRepository;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IWeChatMiniProgramAsyncLocal _weChatMiniProgramAsyncLocal;
        private readonly IMiniProgramUserRepository _miniProgramUserRepository;
        private readonly IMiniProgramLoginNewUserCreator _miniProgramLoginNewUserCreator;
        private readonly IMiniProgramLoginProviderProvider _miniProgramLoginProviderProvider;
        private readonly IDistributedCache<MiniProgramPcLoginAuthorizationCacheItem> _pcLoginAuthorizationCache;
        private readonly IDistributedCache<MiniProgramPcLoginUserLimitCacheItem> _pcLoginUserLimitCache;
        private readonly IOptions<IdentityOptions> _identityOptions;
        private readonly IdentityUserManager _identityUserManager;
        private readonly IMiniProgramRepository _miniProgramRepository;
        public WzhLoginAppService(
            LoginService loginService,
            ACodeService aCodeService,
            SignatureChecker signatureChecker,
            SignInManager<IdentityUser> signInManager,
            IDataFilter dataFilter,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            IUserInfoRepository userInfoRepository,
            IJsonSerializer jsonSerializer,
            IWeChatMiniProgramAsyncLocal weChatMiniProgramAsyncLocal,
            IMiniProgramUserRepository miniProgramUserRepository,
            IMiniProgramLoginNewUserCreator miniProgramLoginNewUserCreator,
            IMiniProgramLoginProviderProvider miniProgramLoginProviderProvider,
            IDistributedCache<MiniProgramPcLoginAuthorizationCacheItem> pcLoginAuthorizationCache,
            IDistributedCache<MiniProgramPcLoginUserLimitCacheItem> pcLoginUserLimitCache,
            IOptions<IdentityOptions> identityOptions,
            IdentityUserManager identityUserManager,
            IMiniProgramRepository miniProgramRepository)
        {
            _loginService = loginService;
            _aCodeService = aCodeService;
            _signatureChecker = signatureChecker;
            _signInManager = signInManager;
            _dataFilter = dataFilter;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _userInfoRepository = userInfoRepository;
            _jsonSerializer = jsonSerializer;
            _weChatMiniProgramAsyncLocal = weChatMiniProgramAsyncLocal;
            _miniProgramUserRepository = miniProgramUserRepository;
            _miniProgramLoginNewUserCreator = miniProgramLoginNewUserCreator;
            _miniProgramLoginProviderProvider = miniProgramLoginProviderProvider;
            _pcLoginAuthorizationCache = pcLoginAuthorizationCache;
            _pcLoginUserLimitCache = pcLoginUserLimitCache;
            _identityOptions = identityOptions;
            _identityUserManager = identityUserManager;
            _miniProgramRepository = miniProgramRepository;
        }
        [Authorize]
        public virtual async Task BindAsync(LoginInput input)
        {
            await CheckBindPolicyAsync();

            var loginResult = await GetLoginResultAsync(input);

            using var tenantChange = CurrentTenant.Change(loginResult.MiniProgram.TenantId);

            await _identityOptions.SetAsync();

            if (await _identityUserManager.FindByLoginAsync(loginResult.LoginProvider, loginResult.ProviderKey) != null)
            {
                throw new WechatAccountHasBeenBoundException();
            }

            var identityUser = await _identityUserManager.GetByIdAsync(CurrentUser.GetId());

            (await _identityUserManager.AddLoginAsync(identityUser,
                new UserLoginInfo(loginResult.LoginProvider, loginResult.ProviderKey,
                    WeChatManagementCommonConsts.WeChatUserLoginInfoDisplayName))).CheckErrors();

            await UpdateMiniProgramUserAsync(identityUser, loginResult.MiniProgram, loginResult.UnionId,
                loginResult.Code2SessionResponse.OpenId, loginResult.Code2SessionResponse.SessionKey);

            await UpdateUserInfoAsync(identityUser, input.UserInfo);
        }

        public virtual async Task<LoginOutput> LoginAsync(LoginInput input)
        {
            var loginResult = await GetLoginResultAsync(input);

            using var tenantChange = CurrentTenant.Change(loginResult.MiniProgram.TenantId);

            await _identityOptions.SetAsync();

            using (var uow = UnitOfWorkManager.Begin(new AbpUnitOfWorkOptions(true), true))
            {
                var identityUser =
                    await _identityUserManager.FindByLoginAsync(loginResult.LoginProvider, loginResult.ProviderKey) ??
                    await _miniProgramLoginNewUserCreator.CreateAsync(input.UserInfo, loginResult.LoginProvider,
                        loginResult.ProviderKey);

                await UpdateMiniProgramUserAsync(identityUser, loginResult.MiniProgram, loginResult.UnionId,
                    loginResult.Code2SessionResponse.OpenId, loginResult.Code2SessionResponse.SessionKey);

                await UpdateUserInfoAsync(identityUser, input.UserInfo);

                await uow.CompleteAsync();
            }

            return new LoginOutput
            {
                TenantId = loginResult.MiniProgram.TenantId,
                RawData = (await RequestIds4LoginAsync(input.AppId, loginResult.UnionId,
                    loginResult.Code2SessionResponse.OpenId))?.Raw
            };
        }

        protected virtual async Task CheckBindPolicyAsync()
        {
            await CheckPolicyAsync(BindPolicyName);
        }

        protected virtual async Task<LoginResultInfoModel> GetLoginResultAsync(LoginInput input)
        {
            var tenantId = CurrentTenant.Id;
            var tenantChanged = false;

            MiniProgram miniProgram;

            if (input.LookupUseRecentlyTenant)
            {
                using (_dataFilter.Disable<IMultiTenant>())
                {
                    miniProgram = await _miniProgramRepository.FirstOrDefaultAsync(x => x.AppId == input.AppId);
                }
            }
            else
            {
                miniProgram = await _miniProgramRepository.GetAsync(x => x.AppId == input.AppId);
            }

            var code2SessionResponse =
                await _loginService.Code2SessionAsync(miniProgram.AppId, miniProgram.AppSecret, input.Code);

            _signatureChecker.Check(input.RawData, code2SessionResponse.SessionKey, input.Signature);

            var openId = code2SessionResponse.OpenId;
            var unionId = code2SessionResponse.UnionId;

            if (input.LookupUseRecentlyTenant)
            {
                using (_dataFilter.Disable<IMultiTenant>())
                {
                    tenantId = await _miniProgramUserRepository.FindRecentlyTenantIdAsync(input.AppId, openId, true);
                }

                if (tenantId != CurrentTenant.Id)
                {
                    tenantChanged = true;
                }
            }

            using var tenantChange = CurrentTenant.Change(tenantId);

            if (tenantChanged)
            {
                miniProgram = await _miniProgramRepository.GetAsync(x => x.AppId == input.AppId);
            }

            // 如果 auth.code2Session 没有返回用户的 UnionId
            if (unionId.IsNullOrWhiteSpace())
            {
                if (!input.EncryptedData.IsNullOrWhiteSpace() && !input.Iv.IsNullOrWhiteSpace())
                {
                    // 方法1：通过 EncryptedData 和 Iv 解密获得用户的 UnionId
                    var decryptedData =
                        _jsonSerializer.Deserialize<Dictionary<string, object>>(
                            AesHelper.AesDecrypt(input.EncryptedData, input.Iv, code2SessionResponse.SessionKey));

                    unionId = decryptedData.GetOrDefault("unionId") as string;
                }
                else
                {
                    // 方法2：尝试通过 OpenId 在 MiniProgramUser 实体中查找用户的 UnionId
                    // Todo: should use IMiniProgramUserStore
                    unionId = await _miniProgramUserRepository.FindUnionIdByOpenIdAsync(miniProgram.Id, openId);
                }
            }

            string loginProvider;
            string providerKey;

            if (unionId.IsNullOrWhiteSpace())
            {
                loginProvider = await _miniProgramLoginProviderProvider.GetAppLoginProviderAsync(miniProgram);
                providerKey = openId;
            }
            else
            {
                loginProvider = await _miniProgramLoginProviderProvider.GetOpenLoginProviderAsync(miniProgram);
                providerKey = unionId;
            }
            return new LoginResultInfoModel
            {
                MiniProgram = miniProgram,
                LoginProvider = loginProvider,
                ProviderKey = providerKey,
                UnionId = unionId,
                Code2SessionResponse = code2SessionResponse
            };
        }

        public virtual async Task<string> RefreshAsync(RefreshInput input)
        {
            return (await RequestIds4RefreshAsync(input.RefreshToken))?.Raw;
        }

        protected virtual async Task UpdateMiniProgramUserAsync(IdentityUser identityUser, MiniProgram miniProgram, string unionId, string openId, string sessionKey)
        {
            var mpUserMapping = await _miniProgramUserRepository.FindAsync(x =>
                x.MiniProgramId == miniProgram.Id && x.UserId == identityUser.Id);

            if (mpUserMapping == null)
            {
                mpUserMapping = new MiniProgramUser(GuidGenerator.Create(), CurrentTenant.Id, miniProgram.Id,
                    identityUser.Id, unionId, openId);

                await _miniProgramUserRepository.InsertAsync(mpUserMapping, true);
            }
            else
            {
                mpUserMapping.SetOpenId(openId);
                mpUserMapping.SetUnionId(unionId);

                mpUserMapping.UpdateSessionKey(sessionKey, Clock);

                await _miniProgramUserRepository.UpdateAsync(mpUserMapping, true);
            }
        }

        protected virtual async Task UpdateUserInfoAsync(IdentityUser identityUser, UserInfoModel userInfoModel)
        {
            var userInfo = await _userInfoRepository.FindAsync(x => x.UserId == identityUser.Id);

            if (userInfo == null)
            {
                userInfo = new UserInfo(GuidGenerator.Create(), CurrentTenant.Id, identityUser.Id, userInfoModel);

                await _userInfoRepository.InsertAsync(userInfo, true);
            }
            else
            {
                userInfo.UpdateInfo(userInfoModel);

                await _userInfoRepository.UpdateAsync(userInfo, true);
            }
        }

        protected virtual async Task<TokenResponse> RequestIds4LoginAsync(string appId, string unionId, string openId)
        {
            var client = _httpClientFactory.CreateClient(WeChatMiniProgramConsts.IdentityServerHttpClientName);

            var request = new TokenRequest
            {
                Address = _configuration["AuthServer:Authority"] + "/connect/token",
                GrantType = WeChatMiniProgramConsts.GrantType,

                ClientId = _configuration["AuthServer:ClientId"],
                ClientSecret = _configuration["AuthServer:ClientSecret"],

                Parameters =
                {
                    {"appid", appId},
                    {"unionid", unionId},
                    {"openid", openId},
                }
            };

            request.Headers.Add(GetTenantHeaderName(), CurrentTenant.Id?.ToString());

            return await client.RequestTokenAsync(request);
        }

        protected virtual async Task<TokenResponse> RequestIds4RefreshAsync(string refreshToken)
        {
            var client = _httpClientFactory.CreateClient(WeChatMiniProgramConsts.IdentityServerHttpClientName);

            var request = new RefreshTokenRequest
            {
                Address = _configuration["AuthServer:Authority"] + "/connect/token",

                ClientId = _configuration["AuthServer:ClientId"],
                ClientSecret = _configuration["AuthServer:ClientSecret"],

                RefreshToken = refreshToken
            };

            request.Headers.Add(GetTenantHeaderName(), CurrentTenant.Id?.ToString());

            return await client.RequestRefreshTokenAsync(request);
        }

        protected virtual string GetTenantHeaderName()
        {
            return "__tenant";
        }

        public virtual async Task<GetPcLoginACodeOutput> GetPcLoginACodeAsync(string miniProgramName)
        {
            var miniProgram = await _miniProgramRepository.GetAsync(x => x.Name == miniProgramName);

            var options = new AbpWeChatMiniProgramOptions
            {
                OpenAppId = miniProgram.OpenAppIdOrName,
                AppId = miniProgram.AppId,
                AppSecret = miniProgram.AppSecret,
                EncodingAesKey = miniProgram.EncodingAesKey,
                Token = miniProgram.Token
            };

            using (_weChatMiniProgramAsyncLocal.Change(options))
            {
                var token = Guid.NewGuid().ToString("N");

                var handlePage = await SettingProvider.GetOrNullAsync(MiniProgramsSettings.PcLogin.HandlePage);

                var aCodeResponse = await _aCodeService.GetUnlimitedACodeAsync(token, handlePage);

                if (aCodeResponse.ErrorCode != 0)
                {
                    throw new WeChatBusinessException(aCodeResponse.ErrorCode, aCodeResponse.ErrorMessage);
                }

                return new GetPcLoginACodeOutput
                {
                    Token = token,
                    ACode = aCodeResponse.BinaryData
                };
            }
        }

        [Authorize]
        public virtual async Task AuthorizePcAsync(AuthorizePcInput input)
        {
            if (await _pcLoginUserLimitCache.GetAsync(CurrentUser.GetId().ToString()) != null)
            {
                throw new PcLoginAuthorizeTooFrequentlyException();
            }

            await _pcLoginAuthorizationCache.SetAsync(input.Token,
                new MiniProgramPcLoginAuthorizationCacheItem { UserId = CurrentUser.GetId() },
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
                });

            await _pcLoginUserLimitCache.SetAsync(CurrentUser.GetId().ToString(),
                new MiniProgramPcLoginUserLimitCacheItem(),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(3)
                });
        }

        public virtual async Task<PcLoginOutput> PcLoginAsync(PcLoginInput input)
        {
            await _identityOptions.SetAsync();

            var cacheItem = await _pcLoginAuthorizationCache.GetAsync(input.Token);

            if (cacheItem == null)
            {
                return new PcLoginOutput { IsSuccess = false };
            }

            await _pcLoginAuthorizationCache.RemoveAsync(input.Token);

            var user = await _identityUserManager.GetByIdAsync(cacheItem.UserId);

            await _signInManager.SignInAsync(user, false);

            return new PcLoginOutput { IsSuccess = true };
        }
        public virtual async Task<PcCodeLoginOutput> PcCodeLoginAsync(PcLoginInput input)
        {
            await _identityOptions.SetAsync();

            var cacheItem = await _pcLoginAuthorizationCache.GetAsync(input.Token);

            if (cacheItem == null)
            {
                return new PcCodeLoginOutput { IsSuccess = false };
            }

            await _pcLoginAuthorizationCache.RemoveAsync(input.Token);

            var user = await _identityUserManager.GetByIdAsync(cacheItem.UserId);

            await _signInManager.SignInAsync(user, false);
            var miniProgramUser = await _miniProgramUserRepository.GetAsync(x => x.Id == user.Id);
            var miniProgram = await _miniProgramRepository.GetAsync(x => x.Id == miniProgramUser.MiniProgramId);
            var rawData = await RequestIds4LoginAsync(miniProgram.AppId, miniProgramUser.UnionId, miniProgramUser.OpenId);
            return new PcCodeLoginOutput { IsSuccess = true,RawData= rawData.Raw };
        }

    }
}
