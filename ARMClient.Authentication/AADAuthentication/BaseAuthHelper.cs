﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ARMClient.Authentication.Contracts;
using ARMClient.Authentication.EnvironmentStorage;
using ARMClient.Authentication.TenantStorage;
using ARMClient.Authentication.TokenStorage;
using ARMClient.Authentication.Utilities;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Newtonsoft.Json.Linq;

namespace ARMClient.Authentication.AADAuthentication
{
    public abstract class BaseAuthHelper : IAuthHelper
    {
        protected readonly ITokenStorage TokenStorage;
        protected readonly ITenantStorage TenantStorage;
        protected readonly IEnvironmentStorage EnvironmentStorage;
        protected BaseAuthHelper(ITokenStorage tokenStorage,
            ITenantStorage tenantStorage, IEnvironmentStorage environmentStorage)
        {
            this.EnvironmentStorage = environmentStorage;
            this.TokenStorage = tokenStorage;
            this.TenantStorage = tenantStorage;
        }

        public AzureEnvironments AzureEnvironments
        {
            get { return this.EnvironmentStorage.GetSavedEnvironment(); }
            set { this.EnvironmentStorage.SaveEnvironment(value); }
        }

        public async Task AcquireTokens()
        {
            this.TokenStorage.ClearCache();
            this.TenantStorage.ClearCache();

            var tokenCache = new CustomTokenCache();
            var cacheInfo = await GetAuthorizationResult(tokenCache, Constants.AADTenantId);
            Utils.Trace.WriteLine(string.Format("Welcome {0} (Tenant: {1})", cacheInfo.DisplayableId, cacheInfo.TenantId));

            var tenantCache = await GetTokenForTenants(tokenCache, cacheInfo);

            this.TokenStorage.SaveCache(tokenCache);
            this.TenantStorage.SaveCache(tenantCache);
        }

        public async Task<TokenCacheInfo> GetToken(string id, string resource)
        {
            if (String.IsNullOrEmpty(id))
            {
                return await GetRecentToken(resource ?? Constants.CSMResource);
            }

            string tenantId = null;
            var tenantCache = this.TenantStorage.GetCache();
            if (tenantCache.ContainsKey(id))
            {
                tenantId = id;
            }

            if (String.IsNullOrEmpty(tenantId))
            {
                foreach (var tenant in tenantCache)
                {
                    if (tenant.Value.subscriptions.Any(s => s.subscriptionId == id))
                    {
                        tenantId = tenant.Key;
                        break;
                    }
                }
            }

            if (String.IsNullOrEmpty(tenantId))
            {
                return await GetRecentToken(resource ?? Constants.CSMResource);
            }

            if (resource == null)
            {
                resource = id == tenantId ? Constants.AADGraphUrls[(int)AzureEnvironments] : Constants.CSMResource;
            }

            var tokenCache = this.TokenStorage.GetCache();
            TokenCacheInfo cacheInfo;
            if (!tokenCache.TryGetValue(tenantId, resource, out cacheInfo))
            {
                return await GetRecentToken(resource);
            }

            if (cacheInfo.ExpiresOn <= DateTimeOffset.UtcNow)
            {
                cacheInfo = await RefreshToken(tokenCache, cacheInfo);
                this.TokenStorage.SaveCache(tokenCache);
            }

            this.TokenStorage.SaveRecentToken(cacheInfo, resource);

            return cacheInfo;
        }

        public async Task<TokenCacheInfo> GetTokenBySpn(string tenantId, string appId, string appKey)
        {
            this.TokenStorage.ClearCache();
            this.TenantStorage.ClearCache();

            var tokenCache = new CustomTokenCache();
            var cacheInfo = GetAuthorizationResultBySpn(tokenCache, tenantId, appId, appKey, Constants.CSMResource);

            var tenantCache = await GetTokenForTenants(tokenCache, cacheInfo, appId: appId, appKey: appKey);

            this.TokenStorage.SaveCache(tokenCache);
            this.TenantStorage.SaveCache(tenantCache);

            return cacheInfo;
        }

        public async Task<TokenCacheInfo> GetTokenByUpn(string username, string password)
        {
            this.TokenStorage.ClearCache();
            this.TenantStorage.ClearCache();

            var tokenCache = new CustomTokenCache();
            var cacheInfo = GetAuthorizationResultByUpn(tokenCache, "common", username, password, Constants.CSMResource);

            var tenantCache = await GetTokenForTenants(tokenCache, cacheInfo, username: username, password: password);

            this.TokenStorage.SaveCache(tokenCache);
            this.TenantStorage.SaveCache(tenantCache);

            return cacheInfo;
        }

        protected async Task<TokenCacheInfo> GetRecentToken(string resource)
        {
            TokenCacheInfo cacheInfo = this.TokenStorage.GetRecentToken(resource);
            if (cacheInfo != null && cacheInfo.ExpiresOn <= DateTimeOffset.UtcNow)
            {
                var tokenCache = this.TokenStorage.GetCache();
                cacheInfo = await RefreshToken(tokenCache, cacheInfo);
                this.TokenStorage.SaveCache(tokenCache);
                this.TokenStorage.SaveRecentToken(cacheInfo, resource);
            }

            return cacheInfo;
        }

        protected async Task<TokenCacheInfo> RefreshToken(CustomTokenCache tokenCache, TokenCacheInfo cacheInfo)
        {
            if (!String.IsNullOrEmpty(cacheInfo.RefreshToken))
            {
                bool reAcquireTokens = false;
                try
                {
                    return await GetAuthorizationResultByRefreshToken(tokenCache, cacheInfo);
                }
                catch (AdalServiceException ex)
                {
                    if (ex.Message.IndexOf("The provided access grant is expired or revoked", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        throw;
                    }

                    reAcquireTokens = true;
                }

                if (reAcquireTokens)
                {
                    await AcquireTokens();
                    cacheInfo = await GetToken(cacheInfo.TenantId, cacheInfo.Resource);
                    tokenCache.Clone(this.TokenStorage.GetCache());
                    return cacheInfo;
                }
            }
            else if (!String.IsNullOrEmpty(cacheInfo.AppId) && !String.IsNullOrEmpty(cacheInfo.AppKey))
            {
                return GetAuthorizationResultBySpn(tokenCache, cacheInfo.TenantId, cacheInfo.AppId, cacheInfo.AppKey, cacheInfo.Resource);
            }

            throw new NotImplementedException();
        }

        public bool IsCacheValid()
        {
            return this.EnvironmentStorage.IsCacheValid() && this.TokenStorage.IsCacheValid() && this.TenantStorage.IsCacheValid();
        }

        public void ClearTokenCache()
        {
            this.TokenStorage.ClearCache();
            this.TenantStorage.ClearCache();
            this.EnvironmentStorage.ClearSavedEnvironment();
        }

        public IEnumerable<string> DumpTokenCache()
        {
            var tokenCache = this.TokenStorage.GetCache();
            var tenantCache = this.TenantStorage.GetCache();
            foreach (var cacheItem in tokenCache.GetValues(Constants.CSMResource))
            {
                var tenantId = cacheItem.TenantId;

                if (Constants.InfrastructureTenantIds.Contains(tenantId))
                {
                    continue;
                }

                var details = tenantCache[tenantId];
                if (!String.IsNullOrEmpty(cacheItem.DisplayableId))
                {
                    yield return string.Format("User: {0}, Tenant: {1} ({2})", cacheItem.DisplayableId, tenantId, details.domain);
                }
                else if (!String.IsNullOrEmpty(cacheItem.AppId))
                {
                    yield return string.Format(String.IsNullOrEmpty(details.domain) ? "App: {0}, Tenant: {1}" : "App: {0}, Tenant: {1} ({2})", cacheItem.AppId, tenantId, details.domain);
                }
                else
                {
                    throw new NotImplementedException();
                }

                var subscriptions = details.subscriptions;
                yield return string.Format("\tThere are {0} subscriptions", subscriptions.Length);

                foreach (var subscription in subscriptions)
                {
                    yield return string.Format("\tSubscription {0} ({1})", subscription.subscriptionId, subscription.displayName);
                }
                yield return string.Empty;
            }
        }

        protected async Task<TokenCacheInfo> GetAuthorizationResultByRefreshToken(CustomTokenCache tokenCache, TokenCacheInfo cacheInfo)
        {
            var azureEnvironment = this.AzureEnvironments;
            var authority = String.Format("{0}/{1}", Constants.AADLoginUrls[(int)azureEnvironment], cacheInfo.TenantId);
            var context = new AuthenticationContext(
                authority: authority,
                validateAuthority: true,
                tokenCache: tokenCache);

            AuthenticationResult result = await context.AcquireTokenByRefreshTokenAsync(
                    refreshToken: cacheInfo.RefreshToken,
                    clientId: Constants.AADClientId,
                    resource: cacheInfo.Resource);

            var ret = new TokenCacheInfo(cacheInfo.Resource, result);
            ret.TenantId = cacheInfo.TenantId;
            ret.DisplayableId = cacheInfo.DisplayableId;
            tokenCache.Add(ret);
            return ret;
        }

        protected Task<TokenCacheInfo> GetAuthorizationResult(CustomTokenCache tokenCache, string tenantId, string user = null, string resource = Constants.CSMResource)
        {
            var tcs = new TaskCompletionSource<TokenCacheInfo>();

            TokenCacheInfo found;
            if (tokenCache.TryGetValue(tenantId, resource, out found))
            {
                tcs.SetResult(found);
                return tcs.Task;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    var azureEnvironment = this.AzureEnvironments;
                    var authority = String.Format("{0}/{1}", Constants.AADLoginUrls[(int)azureEnvironment], tenantId);
                    var context = new AuthenticationContext(
                        authority: authority,
                        validateAuthority: true,
                        tokenCache: tokenCache);

                    AuthenticationResult result = null;
                    if (!string.IsNullOrEmpty(user))
                    {
                        result = context.AcquireToken(
                            resource: resource,
                            clientId: Constants.AADClientId,
                            redirectUri: new Uri(Constants.AADRedirectUri));
                    }
                    else
                    {
                        result = context.AcquireToken(
                            resource: resource,
                            clientId: Constants.AADClientId,
                            redirectUri: new Uri(Constants.AADRedirectUri),
                            promptBehavior: PromptBehavior.Always);
                    }

                    var cacheInfo = new TokenCacheInfo(resource, result);
                    tokenCache.Add(cacheInfo);
                    tcs.TrySetResult(cacheInfo);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Name = "AcquireTokenThread";
            thread.Start();

            return tcs.Task;
        }

        protected TokenCacheInfo GetAuthorizationResultBySpn(CustomTokenCache tokenCache, string tenantId, string appId, string appKey, string resource)
        {
            TokenCacheInfo found;
            if (tokenCache.TryGetValue(tenantId, resource, out found))
            {
                return found;
            }

            var azureEnvironment = this.AzureEnvironments;
            var authority = String.Format("{0}/{1}", Constants.AADLoginUrls[(int)azureEnvironment], tenantId);
            var context = new AuthenticationContext(
                authority: authority,
                validateAuthority: true,
                tokenCache: tokenCache);
            var credential = new ClientCredential(appId, appKey);
            var result = context.AcquireToken(resource, credential);

            var cacheInfo = new TokenCacheInfo(tenantId, appId, appKey, resource, result);
            tokenCache.Add(cacheInfo);
            return cacheInfo;
        }

        protected TokenCacheInfo GetAuthorizationResultByUpn(CustomTokenCache tokenCache, string tenantId, string username, string password, string resource)
        {
            TokenCacheInfo found;
            if (tokenCache.TryGetValue(tenantId, resource, out found))
            {
                return found;
            }

            var azureEnvironment = this.AzureEnvironments;
            var authority = String.Format("{0}/{1}", Constants.AADLoginUrls[(int)azureEnvironment], tenantId);
            var context = new AuthenticationContext(
                authority: authority,
                validateAuthority: true,
                tokenCache: tokenCache);
            var credential = new UserCredential(username, password);
            var result = context.AcquireToken(resource, Constants.AADClientId, credential);

            var cacheInfo = new TokenCacheInfo(resource, result);
            tokenCache.Add(cacheInfo);
            return cacheInfo;
        }

        protected async Task<Dictionary<string, TenantCacheInfo>> GetTokenForTenants(CustomTokenCache tokenCache, TokenCacheInfo cacheInfo,
            string appId = null, string appKey = null, string username = null, string password = null)
        {
            var recentInfo = cacheInfo;
            var tenantIds = await GetTenantIds(cacheInfo);
            var tenantCache = this.TenantStorage.GetCache();
            foreach (var tenantId in tenantIds)
            {
                var info = new TenantCacheInfo
                {
                    tenantId = tenantId,
                    displayName = "unknown",
                    domain = "unknown"
                };

                TokenCacheInfo result = null;
                try
                {
                    if (!String.IsNullOrEmpty(appId) && !String.IsNullOrEmpty(appKey))
                    {
                        result = GetAuthorizationResultBySpn(tokenCache, tenantId: tenantId, appId: appId, appKey: appKey, resource: Constants.CSMResource);
                    }
                    else if (!String.IsNullOrEmpty(username) && !String.IsNullOrEmpty(password))
                    {
                        result = GetAuthorizationResultByUpn(tokenCache, tenantId: tenantId, username: username, password: password, resource: Constants.CSMResource);
                    }
                    else
                    {
                        result = await GetAuthorizationResult(tokenCache, tenantId: tenantId, user: cacheInfo.DisplayableId);
                    }
                }
                catch (Exception ex)
                {
                    Utils.Trace.WriteLine(string.Format("User: {0}, Tenant: {1} {2}", cacheInfo.DisplayableId, tenantId, ex.Message));
                    Utils.Trace.WriteLine(string.Empty);
                    continue;
                }

                try
                {
                    TokenCacheInfo aadToken = null;
                    if (!String.IsNullOrEmpty(appId) && !String.IsNullOrEmpty(appKey))
                    {
                        aadToken = GetAuthorizationResultBySpn(tokenCache, tenantId: tenantId, appId: appId, appKey: appKey, resource: Constants.AADGraphUrls[(int)AzureEnvironments]);
                    }
                    else if (!String.IsNullOrEmpty(username) && !String.IsNullOrEmpty(password))
                    {
                        aadToken = GetAuthorizationResultByUpn(tokenCache, tenantId: tenantId, username: username, password: password, resource: Constants.AADGraphUrls[(int)AzureEnvironments]);
                    }
                    else
                    {
                        aadToken = await GetAuthorizationResult(tokenCache, tenantId: tenantId, user: cacheInfo.DisplayableId, resource: Constants.AADGraphUrls[(int)AzureEnvironments]);
                    }
                    var details = await GetTenantDetail(aadToken, tenantId);
                    info.displayName = details.displayName;
                    info.domain = details.verifiedDomains.First(d => d.@default).name;

                    if (!String.IsNullOrEmpty(appId) && !String.IsNullOrEmpty(appKey))
                    {
                        Utils.Trace.WriteLine(string.Format("AppId: {0}, Tenant: {1} ({2})", appId, tenantId, details.verifiedDomains.First(d => d.@default).name));
                    }
                    else
                    {
                        Utils.Trace.WriteLine(string.Format("User: {0}, Tenant: {1} ({2})", result.DisplayableId, tenantId, details.verifiedDomains.First(d => d.@default).name));
                    }
                }
                catch (Exception ex)
                {
                    if (!String.IsNullOrEmpty(appId) && !String.IsNullOrEmpty(appKey))
                    {
                        Utils.Trace.WriteLine(string.Format("AppId: {0}, Tenant: {1}", appId, tenantId));
                    }
                    else
                    {
                        Utils.Trace.WriteLine(string.Format("User: {0}, Tenant: {1}", result.DisplayableId, tenantId));
                    }
                }

                try
                {
                    var subscriptions = await GetSubscriptions(result);
                    Utils.Trace.WriteLine(string.Format("\tThere are {0} subscriptions", subscriptions.Length));

                    info.subscriptions = subscriptions.Select(subscription => new SubscriptionCacheInfo
                    {
                        subscriptionId = subscription.subscriptionId,
                        displayName = subscription.displayName
                    }).ToArray();

                    if (info.subscriptions.Length > 0)
                    {
                        recentInfo = result;
                    }

                    foreach (var subscription in subscriptions)
                    {
                        Utils.Trace.WriteLine(string.Format("\tSubscription {0} ({1})", subscription.subscriptionId, subscription.displayName));
                    }
                }
                catch (Exception ex)
                {
                    Utils.Trace.WriteLine(string.Format("\t{0}!", ex.Message));
                }
                tenantCache[tenantId] = info;
                Utils.Trace.WriteLine(string.Empty);
            }

            this.TokenStorage.SaveRecentToken(recentInfo, Constants.CSMResource);

            return tenantCache;
        }

        private async Task<string[]> GetTenantIds(TokenCacheInfo cacheInfo)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", cacheInfo.CreateAuthorizationHeader());
                client.DefaultRequestHeaders.Add("User-Agent", Constants.UserAgent.Value);

                var azureEnvironment = this.AzureEnvironments;
                var url = string.Format("{0}/tenants?api-version={1}", Constants.CSMUrls[(int)azureEnvironment], Constants.CSMApiVersion);
                using (var response = await client.GetAsync(url))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsAsync<ResultOf<TenantInfo>>();
                        return result.value.Select(tenant => tenant.tenantId).ToArray();
                    }

                    throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
                }
            }
        }

        private async Task<TenantDetails> GetTenantDetail(TokenCacheInfo cacheInfo, string tenantId)
        {
            if (Constants.InfrastructureTenantIds.Contains(tenantId))
            {
                return new TenantDetails
                {
                    objectId = tenantId,
                    displayName = "Infrastructure",
                    verifiedDomains = new[]
                    {
                        new VerifiedDomain
                        {
                            name = "live.com",
                            @default = true
                        }
                    }
                };
            }

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", cacheInfo.CreateAuthorizationHeader());
                client.DefaultRequestHeaders.Add("User-Agent", Constants.UserAgent.Value);

                var azureEnvironment = this.AzureEnvironments;
                var url = string.Format("{0}/{1}/tenantDetails?api-version={2}", Constants.AADGraphUrls[(int)azureEnvironment], tenantId, Constants.AADGraphApiVersion);
                using (var response = await client.GetAsync(url))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsAsync<ResultOf<TenantDetails>>();
                        return result.value[0];
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    if (content.StartsWith("{"))
                    {
                        var error = (JObject)JObject.Parse(content)["odata.error"];
                        if (error != null)
                        {
                            throw new InvalidOperationException(String.Format("GetTenantDetail {0}, {1}", response.StatusCode, error["message"].Value<string>("value")));
                        }
                    }

                    throw new InvalidOperationException(String.Format("GetTenantDetail {0}, {1}", response.StatusCode, await response.Content.ReadAsStringAsync()));
                }
            }
        }

        private async Task<SubscriptionInfo[]> GetSubscriptions(TokenCacheInfo cacheInfo)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", cacheInfo.CreateAuthorizationHeader());
                client.DefaultRequestHeaders.Add("User-Agent", Constants.UserAgent.Value);

                var azureEnvironment = this.AzureEnvironments;
                var url = string.Format("{0}/subscriptions?api-version={1}", Constants.CSMUrls[(int)azureEnvironment], Constants.CSMApiVersion);
                using (var response = await client.GetAsync(url))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsAsync<ResultOf<SubscriptionInfo>>();
                        return result.value;
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    if (content.StartsWith("{"))
                    {
                        var error = (JObject)JObject.Parse(content)["error"];
                        if (error != null)
                        {
                            throw new InvalidOperationException(String.Format("GetSubscriptions {0}, {1}", response.StatusCode, error.Value<string>("message")));
                        }
                    }

                    throw new InvalidOperationException(String.Format("GetSubscriptions {0}, {1}", response.StatusCode, await response.Content.ReadAsStringAsync()));
                }
            }
        }

    }
}
