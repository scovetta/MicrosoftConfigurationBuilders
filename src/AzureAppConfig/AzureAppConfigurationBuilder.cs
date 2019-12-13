﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See the License.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure;
using Azure.Data.AppConfiguration;
using Azure.Identity;

namespace Microsoft.Configuration.ConfigurationBuilders
{
    /// <summary>
    /// A ConfigurationProvider that retrieves values from Azure App Configuration stores.
    /// </summary>
    public class AzureAppConfigurationBuilder : KeyValueConfigBuilder
    {
        #pragma warning disable CS1591 // No xml comments for tag literals.
        public const string endpointTag = "endpoint";
        public const string connectionStringTag = "connectionString";
        public const string keyFilterTag = "keyFilter";
        public const string labelFilterTag = "labelFilter";
        public const string dateTimeFilterTag = "acceptDateTime";
        #pragma warning restore CS1591 // No xml comments for tag literals.

        private Uri _endpoint;
        private string _connectionString;
        private string _keyFilter;
        private string _labelFilter;
        private DateTimeOffset _dateTimeFilter;

        private ConfigurationClient _client;

        /// <summary>
        /// Initializes the configuration builder lazily.
        /// </summary>
        /// <param name="name">The friendly name of the provider.</param>
        /// <param name="config">A collection of the name/value pairs representing builder-specific attributes specified in the configuration for this provider.</param>
        protected override void LazyInitialize(string name, NameValueCollection config)
        {
            // Default 'Optional' to false. base.LazyInitialize() will override if specified in config.
            Optional = false;

            base.LazyInitialize(name, config);

            // keyFilter
            _keyFilter = UpdateConfigSettingWithAppSettings(keyFilterTag);
            if (String.IsNullOrWhiteSpace(_keyFilter))
                _keyFilter = null;

            // labelFilter
            // Place some restrictions on label filter, similar to the .net core provider.
            // The idea is to restrict queries to one label, and one label only. Even if that
            // one label is the "empty" label. Doing so will remove the decision making process
            // from this builders hands about which key/value/label tuple to choose when there
            // are multiple.
            _labelFilter = UpdateConfigSettingWithAppSettings(labelFilterTag);
            if (String.IsNullOrWhiteSpace(_labelFilter)) {
                _labelFilter = null;
            }
            else if (_labelFilter.Contains('*') || _labelFilter.Contains(',')) {
                throw new ArgumentException("The characters '*' and ',' are not supported in label filters.", labelFilterTag);
            }

            // acceptDateTime
            _dateTimeFilter = DateTimeOffset.TryParse(UpdateConfigSettingWithAppSettings(dateTimeFilterTag), out _dateTimeFilter) ? _dateTimeFilter : DateTimeOffset.MinValue;


            // Always allow 'connectionString' to override black magic. But we expect this to be null most of the time.
            _connectionString = UpdateConfigSettingWithAppSettings(connectionStringTag);
            if (String.IsNullOrWhiteSpace(_connectionString))
            {
                _connectionString = null;

                // Use MSI Connector instead.
                string uri = UpdateConfigSettingWithAppSettings(endpointTag);
                if (!String.IsNullOrWhiteSpace(uri))
                {
                    try
                    {
                        _endpoint = new Uri(uri);
                        _client = new ConfigurationClient(_endpoint, new DefaultAzureCredential());
                    }
                    catch (Exception ex)
                    {
                        if (!Optional)
                            throw new ArgumentException($"Exception encountered while creating connection to Azure App Configuration store.", ex);
                    }
                }
                else
                {
                    throw new ArgumentException($"An endpoint URI or connection string must be provided for connecting to Azure App Configuration service via the '{endpointTag}' or '{connectionStringTag}' attributes.");
                }
            }
            else
            {
                // If we get here, then we should try to connect with a connection string.
                try
                {
                    _client = new ConfigurationClient(_connectionString);
                }
                catch (Exception e) when (Optional && ((e.InnerException is System.Net.Http.HttpRequestException) || (e.InnerException is UnauthorizedAccessException))) { }
            }

            // At this point we've got all our ducks in a row and are ready to go. And we know that
            // we will be used, because this is the 'lazy' initializer. But let's handle one oddball case
            // before we go.
            // If we have a keyFilter set, then we will always query a set of values instead of a single
            // value, regardless of whether we are in strict/expand/greedy mode. But if we're not in
            // greedy mode, then the base KeyValueConfigBuilder will still request each key/value it is
            // interested in one at a time, and only cache that one result. So we will end up querying the
            // same set of values from the AppConfig service for every value. Let's only do this once and
            // cache the entire set to make those calls to GetValueInternal read from the cache instead of
            // hitting the service every time.
            if (_keyFilter != null && Mode != KeyValueMode.Greedy)
                EnsureGreedyInitialized();
        }

        /// <summary>
        /// Makes a determination about whether the input key is valid for this builder and backing store.
        /// </summary>
        /// <param name="key">The string to be validated. May be partial.</param>
        /// <returns>True if the string is valid. False if the string is not a valid key.</returns>
        public override bool ValidateKey(string key)
        {
            // From - https://docs.microsoft.com/en-us/azure/azure-app-configuration/concept-key-value
            // You can use any unicode character in key names entered into App Configuration except for *, ,, and \. These characters are
            // reserved.If you need to include a reserved character, you must escape it by using \{ Reserved Character}.
            if (String.IsNullOrWhiteSpace(key))
                return false;

            if (key.Contains('*') || key.Contains(','))
                return false;

            // We don't want to completely disallow '\' since it is used for escaping. But writing a full parser for someone elses
            // naming format could be error prone. If we see a '\' followed by a '{', just call it good. Don't bother with the Regex
            // if there aren't any backslashes though.
            if (key.Contains('\\'))
                return !Regex.IsMatch(key, @"\\[^{]");

            return true;
        }

        /// <summary>
        /// Looks up a single 'value' for the given 'key.'
        /// </summary>
        /// <param name="key">The 'key' for the secret to look up in the configured Key Vault. (Prefix handling is not needed here.)</param>
        /// <returns>The value corresponding to the given 'key' or null if no value is found.</returns>
        public override string GetValue(string key)
        {
            // Quick shortcut. If we have a keyFilter set, then we've already populated the cache with
            // all possible values for this builder. If we get here, that means the key was not found in
            // the cache. Going further will query with just the key name, and no keyFilter applied. This
            // could result in finding a value... but we shouldn't, because the requested key does not
            // match the keyFilter - otherwise it would already be in the cache. Avoid the trouble and
            // shortcut return nothing in this case.
            if (_keyFilter != null)
                return null;

            // Azure Key Vault keys are case-insensitive, so this should be fine.
            // Also, this is a synchronous method. And in single-threaded contexts like ASP.Net
            // it can be bad/dangerous to block on async calls. So lets work some TPL voodoo
            // to avoid potential deadlocks.
            return Task.Run(async () => { return await GetValueAsync(key); }).Result;
        }

        /// <summary>
        /// Retrieves all known key/value pairs from the Azure App Config store where the key begins with with <paramref name="prefix"/>.
        /// </summary>
        /// <param name="prefix">A prefix string to filter the list of potential keys retrieved from the source.</param>
        /// <returns>A collection of key/value pairs.</returns>
        public override ICollection<KeyValuePair<string, string>> GetAllValues(string prefix)
        {
            // This is also a synchronous method. And in single-threaded contexts like ASP.Net
            // it can be bad/dangerous to block on async calls. So lets work some TPL voodoo
            // again to avoid potential deadlocks.
            return Task.Run(async () => { return await GetAllValuesAsync(prefix); }).Result;
        }

        private async Task<string> GetValueAsync(string key)
        {
            if (_client == null)
                return null;

            SettingSelector selector = new SettingSelector(key, _labelFilter);
            selector.Fields = SettingFields.Key | SettingFields.Value;
            if (_dateTimeFilter > DateTimeOffset.MinValue)
            {
                selector.AcceptDateTime = _dateTimeFilter;
            }

            try
            {
                AsyncPageable<ConfigurationSetting> settings = _client.GetConfigurationSettingsAsync(selector);
                IAsyncEnumerator<ConfigurationSetting> enumerator = settings.GetAsyncEnumerator();

                try
                {
                    // There should only be one result. If there's more, we're only returning the fisrt.
                    await enumerator.MoveNextAsync();
                    return enumerator.Current?.Value;
                }
                finally
                {
                    await enumerator.DisposeAsync();
                }
            }
            catch (Exception e) when (Optional && ((e.InnerException is System.Net.Http.HttpRequestException) || (e.InnerException is UnauthorizedAccessException))) { }

            return null;
        }

        private async Task<ICollection<KeyValuePair<string, string>>> GetAllValuesAsync(string prefix)
        {
            Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (_client == null)
                return data;

            SettingSelector selector = new SettingSelector(_keyFilter, _labelFilter);
            selector.Fields = SettingFields.Key | SettingFields.Value;
            if (_dateTimeFilter > DateTimeOffset.MinValue)
            {
                selector.AcceptDateTime = _dateTimeFilter;
            }

            // We don't make any guarantees about which kv get precendence when there are multiple of the same key...
            // But the config service does seem to return kvs in a preferred order - no label first, then alphabetical by label.
            // Prefer the first kv we encounter from the config service.
            try
            {
                AsyncPageable<ConfigurationSetting> settings = _client.GetConfigurationSettingsAsync(selector);
                IAsyncEnumerator<ConfigurationSetting> enumerator = settings.GetAsyncEnumerator();
                try
                {
                    while (await enumerator.MoveNextAsync())
                    {
                        ConfigurationSetting setting = enumerator.Current;
                        if (!data.ContainsKey(setting.Key))
                            data[setting.Key] = setting.Value;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync();
                }
            }
            catch (Exception e) when (Optional && ((e.InnerException is System.Net.Http.HttpRequestException) || (e.InnerException is UnauthorizedAccessException))) { }

            return data;
        }
    }
}
