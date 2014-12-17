﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ARMClient.Authentication.Contracts;
using ARMClient.Authentication.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ARMClient.Authentication.TenantStorage
{
    class FileTenantStorage : ITenantStorage
    {
        private const string _fileName = "cache_tenants.dat";

        public void SaveCache(Dictionary<string, TenantCacheInfo> tenants)
        {
            var json = JObject.FromObject(tenants);
            ProtectedFile.WriteAllText(ProtectedFile.GetCacheFile(_fileName), json.ToString());
        }

        public Dictionary<string, TenantCacheInfo> GetCache()
        {
            var file = ProtectedFile.GetCacheFile(_fileName);
            if (!File.Exists(file))
            {
                return new Dictionary<string, TenantCacheInfo>();
            }

            return JsonConvert.DeserializeObject<Dictionary<string, TenantCacheInfo>>(ProtectedFile.ReadAllText(file));
        }

        public bool IsCacheValid()
        {
            var cache = GetCache();
            return cache != null && cache.Count > 0;
        }

        public void ClearCache()
        {
            var filePath = ProtectedFile.GetCacheFile(_fileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
