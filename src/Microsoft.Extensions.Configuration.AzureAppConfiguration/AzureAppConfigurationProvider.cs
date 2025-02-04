﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
//
using Azure;
using Azure.Data.AppConfiguration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration.Extensions;
using Microsoft.Extensions.Configuration.AzureAppConfiguration.FeatureManagement;
using Microsoft.Extensions.Configuration.AzureAppConfiguration.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.Configuration.AzureAppConfiguration
{
    internal class AzureAppConfigurationProvider : ConfigurationProvider, IConfigurationRefresher
    {
        private bool _optional;
        private bool _isInitialLoadComplete = false;
        private bool _isFeatureManagementVersionInspected;
        private readonly bool _requestTracingEnabled;
        private readonly IConfigurationClientManager _configClientManager;
        private AzureAppConfigurationOptions _options;
        private Dictionary<string, ConfigurationSetting> _mappedData;
        private Dictionary<KeyValueIdentifier, ConfigurationSetting> _watchedSettings = new Dictionary<KeyValueIdentifier, ConfigurationSetting>();
        private RequestTracingOptions _requestTracingOptions;

        private readonly TimeSpan MinCacheExpirationInterval;

        // The most-recent time when the refresh operation attempted to load the initial configuration
        private DateTimeOffset InitializationCacheExpires = default;

        private static readonly TimeSpan MinDelayForUnhandledFailure = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DefaultMaxSetDirtyDelay = TimeSpan.FromSeconds(30);

        // To avoid concurrent network operations, this flag is used to achieve synchronization between multiple threads.
        private int _networkOperationsInProgress = 0;
        private Logger _logger = new Logger();
        private ILoggerFactory _loggerFactory;

        public Uri AppConfigurationEndpoint
        {
            get
            {
                if (_options.Endpoints != null)
                {
                    return _options.Endpoints.First();
                }

                if (_options.ConnectionStrings != null && _options.ConnectionStrings.Any() && _options.ConnectionStrings.First() != null)
                {
                    // Use try-catch block to avoid throwing exceptions from property getter.
                    // https://docs.microsoft.com/en-us/dotnet/standard/design-guidelines/property

                    try
                    {
                        return new Uri(ConnectionStringParser.Parse(_options.ConnectionStrings.First(), ConnectionStringParser.EndpointSection));
                    }
                    catch (FormatException) { }
                }

                return null;
            }
        }

        public ILoggerFactory LoggerFactory
        {
            get
            {
                return _loggerFactory;
            }
            set
            {
                _loggerFactory = value;

                if (_loggerFactory != null)
                {
                    _logger = new Logger(_loggerFactory.CreateLogger(LoggingConstants.AppConfigRefreshLogCategory));
                }
            }
        }

        public AzureAppConfigurationProvider(IConfigurationClientManager configClientManager, AzureAppConfigurationOptions options, bool optional)
        {
            _configClientManager = configClientManager ?? throw new ArgumentNullException(nameof(configClientManager));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _optional = optional;

            IEnumerable<KeyValueWatcher> watchers = options.ChangeWatchers.Union(options.MultiKeyWatchers);

            if (watchers.Any())
            {
                MinCacheExpirationInterval = watchers.Min(w => w.CacheExpirationInterval);
            }
            else
            {
                MinCacheExpirationInterval = RefreshConstants.DefaultCacheExpirationInterval;
            }

            // Enable request tracing if not opt-out
            string requestTracingDisabled = null;
            try
            {
                requestTracingDisabled = Environment.GetEnvironmentVariable(RequestTracingConstants.RequestTracingDisabledEnvironmentVariable);
            }
            catch (SecurityException) { }
            _requestTracingEnabled = bool.TryParse(requestTracingDisabled, out bool tracingDisabled) ? !tracingDisabled : true;

            if (_requestTracingEnabled)
            {
                SetRequestTracingOptions();
            }
        }

        /// <summary>
        /// Loads (or reloads) the data for this provider.
        /// </summary>
        public override void Load()
        {
            var watch = Stopwatch.StartNew();

            try
            {
                using var startupCancellationTokenSource = new CancellationTokenSource(_options.Startup.Timeout);

                // Load() is invoked only once during application startup. We don't need to check for concurrent network
                // operations here because there can't be any other startup or refresh operation in progress at this time.
                LoadAsync(_optional, startupCancellationTokenSource.Token).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (ArgumentException)
            {
                // Instantly re-throw the exception
                throw;
            }
            catch
            {
                // AzureAppConfigurationProvider.Load() method is called in the application's startup code path.
                // Unhandled exceptions cause application crash which can result in crash loops as orchestrators attempt to restart the application.
                // Knowing the intended usage of the provider in startup code path, we mitigate back-to-back crash loops from overloading the server with requests by waiting a minimum time to propogate fatal errors.

                var waitInterval = MinDelayForUnhandledFailure.Subtract(watch.Elapsed);

                if (waitInterval.Ticks > 0)
                {
                    Task.Delay(waitInterval).ConfigureAwait(false).GetAwaiter().GetResult();
                }

                // Re-throw the exception after the additional delay (if required)
                throw;
            }
            finally
            {
                // Set the provider for AzureAppConfigurationRefresher instance after LoadAll has completed.
                // This stops applications from calling RefreshAsync until config has been initialized during startup.
                var refresher = (AzureAppConfigurationRefresher)_options.GetRefresher();
                refresher.SetProvider(this);
            }

            // Mark all settings have loaded at startup.
            _isInitialLoadComplete = true;
        }

        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            // Ensure that concurrent threads do not simultaneously execute refresh operation.
            if (Interlocked.Exchange(ref _networkOperationsInProgress, 1) == 0)
            {
                try
                {
                    // FeatureManagement assemblies may not be loaded on provider startup, so version information is gathered upon first refresh for tracing
                    EnsureFeatureManagementVersionInspected();

                    var utcNow = DateTimeOffset.UtcNow;
                    IEnumerable<KeyValueWatcher> cacheExpiredWatchers = _options.ChangeWatchers.Where(changeWatcher => utcNow >= changeWatcher.CacheExpires);
                    IEnumerable<KeyValueWatcher> cacheExpiredMultiKeyWatchers = _options.MultiKeyWatchers.Where(changeWatcher => utcNow >= changeWatcher.CacheExpires);

                    // Skip refresh if mappedData is loaded, but none of the watchers or adapters cache is expired.
                    if (_mappedData != null &&
                        !cacheExpiredWatchers.Any() &&
                        !cacheExpiredMultiKeyWatchers.Any() &&
                        !_options.Adapters.Any(adapter => adapter.NeedsRefresh()))
                    {
                        return;
                    }

                    IEnumerable<ConfigurationClient> availableClients = _configClientManager.GetAvailableClients(utcNow);

                    if (!availableClients.Any())
                    {
                        _logger.LogDebug(LogHelper.BuildRefreshSkippedNoClientAvailableMessage());
                        return;
                    }

                    // Check if initial configuration load had failed
                    if (_mappedData == null)
                    {
                        if (InitializationCacheExpires < utcNow)
                        {
                            InitializationCacheExpires = utcNow.Add(MinCacheExpirationInterval);

                            await InitializeAsync(availableClients, cancellationToken).ConfigureAwait(false);
                        }

                        return;
                    }

                    //
                    // Avoid instance state modification
                    Dictionary<KeyValueIdentifier, ConfigurationSetting> watchedSettings = null;
                    List<KeyValueChange> keyValueChanges = null;
                    List<KeyValueChange> changedKeyValuesCollection = null;
                    Dictionary<string, ConfigurationSetting> data = null;
                    bool refreshAll = false;
                    StringBuilder logInfoBuilder = new StringBuilder();
                    StringBuilder logDebugBuilder = new StringBuilder();

                    await ExecuteWithFailOverPolicyAsync(availableClients, async (client) =>
                        {
                            data = null;
                            watchedSettings = null;
                            keyValueChanges = new List<KeyValueChange>();
                            changedKeyValuesCollection = null;
                            refreshAll = false;
                            Uri endpoint = _configClientManager.GetEndpointForClient(client);
                            logDebugBuilder.Clear();
                            logInfoBuilder.Clear();

                            foreach (KeyValueWatcher changeWatcher in cacheExpiredWatchers)
                            {
                                string watchedKey = changeWatcher.Key;
                                string watchedLabel = changeWatcher.Label;

                                KeyValueIdentifier watchedKeyLabel = new KeyValueIdentifier(watchedKey, watchedLabel);

                                KeyValueChange change = default;

                                //
                                // Find if there is a change associated with watcher
                                if (_watchedSettings.TryGetValue(watchedKeyLabel, out ConfigurationSetting watchedKv))
                                {
                                    await TracingUtils.CallWithRequestTracing(_requestTracingEnabled, RequestType.Watch, _requestTracingOptions,
                                        async () => change = await client.GetKeyValueChange(watchedKv, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                                }
                                else
                                {
                                    // Load the key-value in case the previous load attempts had failed

                                    try
                                    {
                                        await CallWithRequestTracing(
                                            async () => watchedKv = await client.GetConfigurationSettingAsync(watchedKey, watchedLabel, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                                    }
                                    catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.NotFound)
                                    {
                                        watchedKv = null;
                                    }

                                    if (watchedKv != null)
                                    {
                                        change = new KeyValueChange()
                                        {
                                            Key = watchedKv.Key,
                                            Label = watchedKv.Label.NormalizeNull(),
                                            Current = watchedKv,
                                            ChangeType = KeyValueChangeType.Modified
                                        };
                                    }
                                }

                                // Check if a change has been detected in the key-value registered for refresh
                                if (change.ChangeType != KeyValueChangeType.None)
                                {
                                    logDebugBuilder.AppendLine(LogHelper.BuildKeyValueReadMessage(change.ChangeType, change.Key, change.Label, endpoint.ToString()));
                                    logInfoBuilder.AppendLine(LogHelper.BuildKeyValueSettingUpdatedMessage(change.Key));
                                    keyValueChanges.Add(change);

                                    if (changeWatcher.RefreshAll)
                                    {
                                        refreshAll = true;
                                        break;
                                    }
                                } else
                                {
                                    logDebugBuilder.AppendLine(LogHelper.BuildKeyValueReadMessage(change.ChangeType, change.Key, change.Label, endpoint.ToString()));
                                }
                            }

                            if (refreshAll)
                            {
                                // Trigger a single load-all operation if a change was detected in one or more key-values with refreshAll: true
                                data = await LoadSelectedKeyValues(client, cancellationToken).ConfigureAwait(false);
                                watchedSettings = await LoadKeyValuesRegisteredForRefresh(client, data, cancellationToken).ConfigureAwait(false);
                                watchedSettings = UpdateWatchedKeyValueCollections(watchedSettings, data);
                                logInfoBuilder.AppendLine(LogHelper.BuildConfigurationUpdatedMessage());
                                return;
                            }

                            changedKeyValuesCollection = await GetRefreshedKeyValueCollections(cacheExpiredMultiKeyWatchers, client, logDebugBuilder, logInfoBuilder, endpoint, cancellationToken).ConfigureAwait(false);

                            if (!changedKeyValuesCollection.Any())
                            {
                                logDebugBuilder.AppendLine(LogHelper.BuildFeatureFlagsUnchangedMessage(endpoint.ToString()));
                            }
                        },
                        cancellationToken)
                        .ConfigureAwait(false);

                    if (!refreshAll)
                    {
                        watchedSettings = new Dictionary<KeyValueIdentifier, ConfigurationSetting>(_watchedSettings);

                        foreach (KeyValueWatcher changeWatcher in cacheExpiredWatchers.Concat(cacheExpiredMultiKeyWatchers))
                        {
                            UpdateCacheExpirationTime(changeWatcher);
                        }

                        foreach (KeyValueChange change in keyValueChanges.Concat(changedKeyValuesCollection))
                        {
                            KeyValueIdentifier changeIdentifier = new KeyValueIdentifier(change.Key, change.Label);
                            if (change.ChangeType == KeyValueChangeType.Modified)
                            {
                                ConfigurationSetting setting = change.Current;
                                ConfigurationSetting settingCopy = new ConfigurationSetting(setting.Key, setting.Value, setting.Label, setting.ETag);
                                watchedSettings[changeIdentifier] = settingCopy;

                                foreach (Func<ConfigurationSetting, ValueTask<ConfigurationSetting>> func in _options.Mappers)
                                {
                                    setting = await func(setting).ConfigureAwait(false);
                                }

                                if (setting == null)
                                {
                                    _mappedData.Remove(change.Key);
                                }
                                else
                                {
                                    _mappedData[change.Key] = setting;
                                }
                            }
                            else if (change.ChangeType == KeyValueChangeType.Deleted)
                            {
                                _mappedData.Remove(change.Key);
                                watchedSettings.Remove(changeIdentifier);
                            }

                            // Invalidate the cached Key Vault secret (if any) for this ConfigurationSetting
                            foreach (IKeyValueAdapter adapter in _options.Adapters)
                            {
                                adapter.InvalidateCache(change.Current);
                            }
                        }

                    }
                    else
                    {
                        _mappedData = await MapConfigurationSettings(data).ConfigureAwait(false);

                        // Invalidate all the cached KeyVault secrets
                        foreach (IKeyValueAdapter adapter in _options.Adapters)
                        {
                            adapter.InvalidateCache();
                        }

                        // Update the cache expiration time for all refresh registered settings and feature flags
                        foreach (KeyValueWatcher changeWatcher in _options.ChangeWatchers.Concat(_options.MultiKeyWatchers))
                        {
                            UpdateCacheExpirationTime(changeWatcher);
                        }
                    }

                    if (_options.Adapters.Any(adapter => adapter.NeedsRefresh()) || changedKeyValuesCollection?.Any() == true || keyValueChanges.Any())
                    {
                        _watchedSettings = watchedSettings;

                        if (logDebugBuilder.Length > 0)
                        {
                            _logger.LogDebug(logDebugBuilder.ToString().Trim());
                        }

                        if (logInfoBuilder.Length > 0)
                        {
                            _logger.LogInformation(logInfoBuilder.ToString().Trim());
                        }
                        // PrepareData makes calls to KeyVault and may throw exceptions. But, we still update watchers before
                        // SetData because repeating appconfig calls (by not updating watchers) won't help anything for keyvault calls.
                        // As long as adapter.NeedsRefresh is true, we will attempt to update keyvault again the next time RefreshAsync is called.
                        SetData(await PrepareData(_mappedData, cancellationToken).ConfigureAwait(false));
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _networkOperationsInProgress, 0);
                }
            }
        }

        public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException rfe)
            {
                if (IsAuthenticationError(rfe))
                {
                    _logger.LogWarning(LogHelper.BuildRefreshFailedDueToAuthenticationErrorMessage(rfe.Message));
                }
                else
                {
                    _logger.LogWarning(LogHelper.BuildRefreshFailedErrorMessage(rfe.Message));
                }

                return false;
            }
            catch (KeyVaultReferenceException kvre)
            {
                _logger.LogWarning(LogHelper.BuildRefreshFailedDueToKeyVaultErrorMessage(kvre.Message));
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogHelper.BuildRefreshCanceledErrorMessage());
                return false;
            }
            catch (InvalidOperationException e)
            {
                _logger.LogWarning(LogHelper.BuildRefreshFailedErrorMessage(e.Message));
                return false;
            }
            catch (AggregateException ae)
            {
                if (ae.InnerExceptions?.Any(e => e is RequestFailedException) ?? false)
                {
                    if (IsAuthenticationError(ae))
                    {
                        _logger.LogWarning(LogHelper.BuildRefreshFailedDueToAuthenticationErrorMessage(ae.Message));
                    }
                    else
                    {
                        _logger.LogWarning(LogHelper.BuildRefreshFailedErrorMessage(ae.Message));
                    }
                }
                else if (ae.InnerExceptions?.Any(e => e is OperationCanceledException) ?? false)
                {
                    _logger.LogWarning(LogHelper.BuildRefreshCanceledErrorMessage());
                }
                else
                {
                    throw;
                }

                return false;
            }

            return true;
        }

        public void ProcessPushNotification(PushNotification pushNotification, TimeSpan? maxDelay)
        {
            if (pushNotification == null)
            {
                throw new ArgumentNullException(nameof(pushNotification));
            }

            if (string.IsNullOrEmpty(pushNotification.SyncToken))
            {
                throw new ArgumentException(
                    "Sync token is required.",
                    $"{nameof(pushNotification)}.{nameof(pushNotification.SyncToken)}");
            }

            if (string.IsNullOrEmpty(pushNotification.EventType))
            {
                throw new ArgumentException(
                    "Event type is required.",
                    $"{nameof(pushNotification)}.{nameof(pushNotification.EventType)}");
            }

            if (pushNotification.ResourceUri == null)
            {
                throw new ArgumentException(
                    "Resource URI is required.",
                    $"{nameof(pushNotification)}.{nameof(pushNotification.ResourceUri)}");
            }

            if (_configClientManager.UpdateSyncToken(pushNotification.ResourceUri, pushNotification.SyncToken))
            {
                SetDirty(maxDelay);
            }
            else
            {
                _logger.LogWarning(LogHelper.BuildPushNotificationUnregisteredEndpointMessage(pushNotification.ResourceUri.ToString()));
            }
        }

        private void SetDirty(TimeSpan? maxDelay)
        {
            DateTimeOffset cacheExpires = AddRandomDelay(DateTimeOffset.UtcNow, maxDelay ?? DefaultMaxSetDirtyDelay);

            foreach (KeyValueWatcher changeWatcher in _options.ChangeWatchers)
            {
                changeWatcher.CacheExpires = cacheExpires;
            }

            foreach (KeyValueWatcher changeWatcher in _options.MultiKeyWatchers)
            {
                changeWatcher.CacheExpires = cacheExpires;
            }
        }

        private async Task<Dictionary<string, string>> PrepareData(Dictionary<string, ConfigurationSetting> data, CancellationToken cancellationToken = default)
        {
            var applicationData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Reset old filter tracing in order to track the filter types present in the current response from server.
            _options.FeatureFilterTracing.ResetFeatureFilterTracing();

            foreach (KeyValuePair<string, ConfigurationSetting> kvp in data)
            {
                IEnumerable<KeyValuePair<string, string>> keyValuePairs = null;
                keyValuePairs = await ProcessAdapters(kvp.Value, cancellationToken).ConfigureAwait(false);

                foreach (KeyValuePair<string, string> kv in keyValuePairs)
                {
                    string key = kv.Key;

                    foreach (string prefix in _options.KeyPrefixes)
                    {
                        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            key = key.Substring(prefix.Length);
                            break;
                        }
                    }

                    applicationData[key] = kv.Value;
                }
            }

            return applicationData;
        }

        private async Task LoadAsync(bool ignoreFailures, CancellationToken cancellationToken)
        {
            var startupStopwatch = Stopwatch.StartNew();

            int postFixedWindowAttempts = 0;

            var startupExceptions = new List<Exception>();

            try
            {
                while (true)
                {
                    IEnumerable<ConfigurationClient> clients = _configClientManager.GetAllClients();

                    if (await TryInitializeAsync(clients, startupExceptions, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    TimeSpan delay;

                    if (startupStopwatch.Elapsed.TryGetFixedBackoff(out TimeSpan backoff))
                    {
                        delay = backoff;
                    }
                    else
                    {
                        postFixedWindowAttempts++;

                        delay = FailOverConstants.MinStartupBackoffDuration.CalculateBackoffDuration(
                            FailOverConstants.MaxBackoffDuration,
                            postFixedWindowAttempts);
                    }

                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException(
                            $"The provider timed out while attempting to load.",
                            new AggregateException(startupExceptions));
                    }
                }
            }
            catch (Exception exception) when (
                ignoreFailures &&
                (exception is RequestFailedException ||
                exception is KeyVaultReferenceException ||
                exception is TimeoutException ||
                exception is OperationCanceledException ||
                exception is InvalidOperationException ||
                ((exception as AggregateException)?.InnerExceptions?.Any(e =>
                    e is RequestFailedException ||
                    e is OperationCanceledException) ?? false)))
            { }
        }

        private async Task<bool> TryInitializeAsync(IEnumerable<ConfigurationClient> clients, List<Exception> startupExceptions, CancellationToken cancellationToken = default)
        {
            try
            {
                await InitializeAsync(clients, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (RequestFailedException exception)
            {
                if (IsFailOverable(exception))
                {
                    startupExceptions.Add(exception);

                    return false;
                }

                throw;
            }
            catch (AggregateException exception)
            {
                if (exception.InnerExceptions?.Any(e => e is OperationCanceledException) ?? false)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        startupExceptions.Add(exception);
                    }

                    return false;
                }

                if (IsFailOverable(exception))
                {
                    startupExceptions.Add(exception);

                    return false;
                }
                
                throw;
            }

            return true;
        }

        private async Task InitializeAsync(IEnumerable<ConfigurationClient> clients, CancellationToken cancellationToken = default)
        {
            Dictionary<string, ConfigurationSetting> data = null;
            Dictionary<KeyValueIdentifier, ConfigurationSetting> watchedSettings = null;

            await ExecuteWithFailOverPolicyAsync(
                clients,
                async (client) =>
                {
                    data = await LoadSelectedKeyValues(
                        client,
                        cancellationToken)
                        .ConfigureAwait(false);

                    watchedSettings = await LoadKeyValuesRegisteredForRefresh(
                        client,
                        data,
                        cancellationToken)
                        .ConfigureAwait(false);

                    watchedSettings = UpdateWatchedKeyValueCollections(watchedSettings, data);
                },
                cancellationToken)
                .ConfigureAwait(false);

            // Update the cache expiration time for all refresh registered settings and feature flags
            foreach (KeyValueWatcher changeWatcher in _options.ChangeWatchers.Concat(_options.MultiKeyWatchers))
            {
                UpdateCacheExpirationTime(changeWatcher);
            }

            if (data != null)
            {
                // Invalidate all the cached KeyVault secrets
                foreach (IKeyValueAdapter adapter in _options.Adapters)
                {
                    adapter.InvalidateCache();
                }

                Dictionary<string, ConfigurationSetting> mappedData = await MapConfigurationSettings(data).ConfigureAwait(false);
                SetData(await PrepareData(mappedData, cancellationToken).ConfigureAwait(false));
                _watchedSettings = watchedSettings;
                _mappedData = mappedData;
            }
        }

        private async Task<Dictionary<string, ConfigurationSetting>> LoadSelectedKeyValues(ConfigurationClient client, CancellationToken cancellationToken)
        {
            var serverData = new Dictionary<string, ConfigurationSetting>(StringComparer.OrdinalIgnoreCase);

            // Use default query if there are no key-values specified for use other than the feature flags
            bool useDefaultQuery = !_options.KeyValueSelectors.Any(selector => selector.KeyFilter == null ||
                !selector.KeyFilter.StartsWith(FeatureManagementConstants.FeatureFlagMarker));

            if (useDefaultQuery)
            {
                // Load all key-values with the null label.
                var selector = new SettingSelector
                {
                    KeyFilter = KeyFilter.Any,
                    LabelFilter = LabelFilter.Null
                };

                await CallWithRequestTracing(async () =>
                {
                    await foreach (ConfigurationSetting setting in client.GetConfigurationSettingsAsync(selector, cancellationToken).ConfigureAwait(false))
                    {
                        serverData[setting.Key] = setting;
                    }
                }).ConfigureAwait(false);
            }

            foreach (KeyValueSelector loadOption in _options.KeyValueSelectors)
            {
                IAsyncEnumerable<ConfigurationSetting> settingsEnumerable;

                if (string.IsNullOrEmpty(loadOption.SnapshotName))
                {
                    settingsEnumerable = client.GetConfigurationSettingsAsync(
                        new SettingSelector
                        {
                            KeyFilter = loadOption.KeyFilter,
                            LabelFilter = loadOption.LabelFilter
                        },
                        cancellationToken);
                }
                else
                {
                    ConfigurationSnapshot snapshot;

                    try
                    {
                        snapshot = await client.GetSnapshotAsync(loadOption.SnapshotName).ConfigureAwait(false);
                    }
                    catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
                    {
                        throw new InvalidOperationException($"Could not find snapshot with name '{loadOption.SnapshotName}'.", rfe);
                    }

                    if (snapshot.SnapshotComposition != SnapshotComposition.Key)
                    {
                        throw new InvalidOperationException($"{nameof(snapshot.SnapshotComposition)} for the selected snapshot with name '{snapshot.Name}' must be 'key', found '{snapshot.SnapshotComposition}'.");
                    }

                    settingsEnumerable = client.GetConfigurationSettingsForSnapshotAsync(
                        loadOption.SnapshotName,
                        cancellationToken);
                }

                await CallWithRequestTracing(async () =>
                {
                    await foreach (ConfigurationSetting setting in settingsEnumerable.ConfigureAwait(false))
                    {
                        serverData[setting.Key] = setting;
                    }
                }).ConfigureAwait(false);
            }

            return serverData;
        }

        private async Task<Dictionary<KeyValueIdentifier, ConfigurationSetting>> LoadKeyValuesRegisteredForRefresh(ConfigurationClient client, IDictionary<string, ConfigurationSetting> existingSettings, CancellationToken cancellationToken)
        {
            Dictionary<KeyValueIdentifier, ConfigurationSetting> watchedSettings = new Dictionary<KeyValueIdentifier, ConfigurationSetting>();

            foreach (KeyValueWatcher changeWatcher in _options.ChangeWatchers)
            {
                string watchedKey = changeWatcher.Key;
                string watchedLabel = changeWatcher.Label;
                KeyValueIdentifier watchedKeyLabel = new KeyValueIdentifier(watchedKey, watchedLabel);

                // Skip the loading for the key-value in case it has already been loaded
                if (existingSettings.TryGetValue(watchedKey, out ConfigurationSetting loadedKv)
                    && watchedKeyLabel.Equals(new KeyValueIdentifier(loadedKv.Key, loadedKv.Label)))
                {
                    watchedSettings[watchedKeyLabel] = new ConfigurationSetting(loadedKv.Key, loadedKv.Value, loadedKv.Label, loadedKv.ETag);
                    continue;
                }

                // Send a request to retrieve key-value since it may be either not loaded or loaded with a different label or different casing
                ConfigurationSetting watchedKv = null;
                try
                {
                    await CallWithRequestTracing(async () => watchedKv = await client.GetConfigurationSettingAsync(watchedKey, watchedLabel, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                }
                catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.NotFound)
                {
                    watchedKv = null;
                }

                // If the key-value was found, store it for updating the settings
                if (watchedKv != null)
                {
                    watchedSettings[watchedKeyLabel] = new ConfigurationSetting(watchedKv.Key, watchedKv.Value, watchedKv.Label, watchedKv.ETag);
                    existingSettings[watchedKey] = watchedKv;
                }
            }

            return watchedSettings;
        }

        private Dictionary<KeyValueIdentifier, ConfigurationSetting> UpdateWatchedKeyValueCollections(Dictionary<KeyValueIdentifier, ConfigurationSetting> watchedSettings, IDictionary<string, ConfigurationSetting> existingSettings)
        {
            foreach (KeyValueWatcher changeWatcher in _options.MultiKeyWatchers)
            {
                IEnumerable<ConfigurationSetting> currentKeyValues = GetCurrentKeyValueCollection(changeWatcher.Key, changeWatcher.Label, existingSettings.Values);

                foreach (ConfigurationSetting setting in currentKeyValues)
                {
                    watchedSettings[new KeyValueIdentifier(setting.Key, setting.Label)] = new ConfigurationSetting(setting.Key, setting.Value, setting.Label, setting.ETag);
                }
            }

            return watchedSettings;
        }

        private async Task<List<KeyValueChange>> GetRefreshedKeyValueCollections(
            IEnumerable<KeyValueWatcher> multiKeyWatchers,
            ConfigurationClient client,
            StringBuilder logDebugBuilder,
            StringBuilder logInfoBuilder,
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            var keyValueChanges = new List<KeyValueChange>();

            foreach (KeyValueWatcher changeWatcher in multiKeyWatchers)
            {
                IEnumerable<ConfigurationSetting> currentKeyValues = GetCurrentKeyValueCollection(changeWatcher.Key, changeWatcher.Label, _watchedSettings.Values);

                keyValueChanges.AddRange(
                    await client.GetKeyValueChangeCollection(
                        currentKeyValues,
                        new GetKeyValueChangeCollectionOptions
                        {
                            KeyFilter = changeWatcher.Key,
                            Label = changeWatcher.Label.NormalizeNull(),
                            RequestTracingEnabled = _requestTracingEnabled,
                            RequestTracingOptions = _requestTracingOptions
                        },
                        logDebugBuilder,
                        logInfoBuilder,
                        endpoint,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            return keyValueChanges;
        }

        private void SetData(IDictionary<string, string> data)
        {
            // Set the application data for the configuration provider
            Data = data;

            // Notify that the configuration has been updated
            OnReload();
        }

        private async Task<IEnumerable<KeyValuePair<string, string>>> ProcessAdapters(ConfigurationSetting setting, CancellationToken cancellationToken)
        {
            List<KeyValuePair<string, string>> keyValues = null;

            foreach (IKeyValueAdapter adapter in _options.Adapters)
            {
                if (!adapter.CanProcess(setting))
                {
                    continue;
                }

                IEnumerable<KeyValuePair<string, string>> kvs = await adapter.ProcessKeyValue(setting, _logger, cancellationToken).ConfigureAwait(false);

                if (kvs != null)
                {
                    keyValues = keyValues ?? new List<KeyValuePair<string, string>>();

                    keyValues.AddRange(kvs);
                }
            }

            return keyValues ?? Enumerable.Repeat(new KeyValuePair<string, string>(setting.Key, setting.Value), 1);
        }

        private Task CallWithRequestTracing(Func<Task> clientCall)
        {
            var requestType = _isInitialLoadComplete ? RequestType.Watch : RequestType.Startup;
            return TracingUtils.CallWithRequestTracing(_requestTracingEnabled, requestType, _requestTracingOptions, clientCall);
        }

        private void SetRequestTracingOptions()
        {
            _requestTracingOptions = new RequestTracingOptions
            {
                HostType = TracingUtils.GetHostType(),
                IsDevEnvironment = TracingUtils.IsDevEnvironment(),
                IsKeyVaultConfigured = _options.IsKeyVaultConfigured,
                IsKeyVaultRefreshConfigured = _options.IsKeyVaultRefreshConfigured,
                ReplicaCount = _options.Endpoints?.Count() - 1 ?? _options.ConnectionStrings?.Count() - 1 ?? 0,
                FilterTracing = _options.FeatureFilterTracing
            };
        }

        private DateTimeOffset AddRandomDelay(DateTimeOffset dt, TimeSpan maxDelay)
        {
            long randomTicks = (long)(maxDelay.Ticks * RandomGenerator.NextDouble());
            return dt.AddTicks(randomTicks);
        }

        private bool IsAuthenticationError(Exception ex)
        {
            if (ex is RequestFailedException rfe)
            {
                return rfe.Status == (int)HttpStatusCode.Unauthorized || rfe.Status == (int)HttpStatusCode.Forbidden;
            }

            if (ex is AggregateException ae)
            {
                return ae.InnerExceptions?.Any(inner => IsAuthenticationError(inner)) ?? false;
            }

            return false;
        }

        private void UpdateCacheExpirationTime(KeyValueWatcher changeWatcher)
        {
            TimeSpan cacheExpirationTime = changeWatcher.CacheExpirationInterval;
            changeWatcher.CacheExpires = DateTimeOffset.UtcNow.Add(cacheExpirationTime);
        }

        private async Task<T> ExecuteWithFailOverPolicyAsync<T>(
            IEnumerable<ConfigurationClient> clients,
            Func<ConfigurationClient, Task<T>> funcToExecute,
            CancellationToken cancellationToken = default)
        {
            using IEnumerator<ConfigurationClient> clientEnumerator = clients.GetEnumerator();

            clientEnumerator.MoveNext();

            Uri previousEndpoint = _configClientManager.GetEndpointForClient(clientEnumerator.Current);
            ConfigurationClient currentClient;

            while (true)
            {
                bool success = false;
                bool backoffAllClients = false;

                cancellationToken.ThrowIfCancellationRequested();
                currentClient = clientEnumerator.Current;

                try
                {
                    T result = await funcToExecute(currentClient).ConfigureAwait(false);
                    success = true;

                    return result;
                }
                catch (RequestFailedException rfe)
                {
                    if (!IsFailOverable(rfe) || !clientEnumerator.MoveNext())
                    {
                        backoffAllClients = true;

                        throw;
                    }
                }
                catch (AggregateException ae)
                {
                    if (!IsFailOverable(ae) || !clientEnumerator.MoveNext())
                    {
                        backoffAllClients = true;

                        throw;
                    }
                }
                finally
                {
                    if (!success && backoffAllClients)
                    {
                        _logger.LogWarning(LogHelper.BuildLastEndpointFailedMessage(previousEndpoint?.ToString()));

                        do
                        {
                            _configClientManager.UpdateClientStatus(currentClient, success);
                            clientEnumerator.MoveNext();
                            currentClient = clientEnumerator.Current;
                        }
                        while (currentClient != null);
                    }
                    else
                    {
                        _configClientManager.UpdateClientStatus(currentClient, success);
                    }
                }

                Uri currentEndpoint = _configClientManager.GetEndpointForClient(clientEnumerator.Current);

                if (previousEndpoint != currentEndpoint)
                {
                    _logger.LogWarning(LogHelper.BuildFailoverMessage(previousEndpoint?.ToString(), currentEndpoint?.ToString()));
                }

                previousEndpoint = currentEndpoint;
            }
        }

        private async Task ExecuteWithFailOverPolicyAsync(
            IEnumerable<ConfigurationClient> clients,
            Func<ConfigurationClient, Task> funcToExecute,
            CancellationToken cancellationToken = default)
        {
            await ExecuteWithFailOverPolicyAsync<object>(clients, async (client) =>
            {
                await funcToExecute(client).ConfigureAwait(false);
                return null;

            }, cancellationToken).ConfigureAwait(false);
        }

        private bool IsFailOverable(AggregateException ex)
        {
            RequestFailedException rfe = ex.InnerExceptions?.LastOrDefault(e => e is RequestFailedException) as RequestFailedException;

            return rfe != null ? IsFailOverable(rfe) : false;
        }

        private bool IsFailOverable(RequestFailedException rfe)
        {
            if (rfe.Status == HttpStatusCodes.TooManyRequests ||
                rfe.Status == (int)HttpStatusCode.RequestTimeout ||
                rfe.Status >= (int)HttpStatusCode.InternalServerError)
            {
                return true;
            }

            Exception innerException;

            if (rfe.InnerException is HttpRequestException hre)
            {
                innerException = hre.InnerException;
            }
            else
            {
                innerException = rfe.InnerException;
            }

            // The InnerException could be SocketException or WebException when an endpoint is invalid and IOException if it's a network issue.
            return innerException is WebException ||
                   innerException is SocketException ||
                   innerException is IOException;
        }

        private async Task<Dictionary<string, ConfigurationSetting>> MapConfigurationSettings(Dictionary<string, ConfigurationSetting> data)
        {
            Dictionary<string, ConfigurationSetting> mappedData = new Dictionary<string, ConfigurationSetting>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, ConfigurationSetting> kvp in data)
            {
                ConfigurationSetting setting = kvp.Value;

                foreach (Func<ConfigurationSetting, ValueTask<ConfigurationSetting>> func in _options.Mappers)
                {
                    setting = await func(setting).ConfigureAwait(false);
                }

                if (setting != null)
                {
                    mappedData[kvp.Key] = setting;
                }
            }

            return mappedData;
        }

        private IEnumerable<ConfigurationSetting> GetCurrentKeyValueCollection(string key, string label, IEnumerable<ConfigurationSetting> existingSettings)
        {
            IEnumerable<ConfigurationSetting> currentKeyValues;

            if (key.EndsWith("*"))
            {
                // Get current application settings starting with changeWatcher.Key, excluding the last * character
                string keyPrefix = key.Substring(0, key.Length - 1);
                currentKeyValues = existingSettings.Where(kv =>
                {
                    return kv.Key.StartsWith(keyPrefix) && kv.Label == label.NormalizeNull();
                });
            }
            else
            {
                currentKeyValues = existingSettings.Where(kv =>
                {
                    return kv.Key.Equals(key) && kv.Label == label.NormalizeNull();
                });
            }

            return currentKeyValues;
        }

        private void EnsureFeatureManagementVersionInspected()
        {
            if (!_isFeatureManagementVersionInspected)
            {
                _isFeatureManagementVersionInspected = true;

                if (_requestTracingEnabled && _requestTracingOptions != null)
                {
                    _requestTracingOptions.FeatureManagementVersion = TracingUtils.GetAssemblyVersion(RequestTracingConstants.FeatureManagementAssemblyName);

                    _requestTracingOptions.FeatureManagementAspNetCoreVersion = TracingUtils.GetAssemblyVersion(RequestTracingConstants.FeatureManagementAspNetCoreAssemblyName);
                }
            }
        }
    }
}
