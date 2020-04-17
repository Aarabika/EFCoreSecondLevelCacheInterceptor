using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EFCoreSecondLevelCacheInterceptor
{
    /// <summary>
    /// Cache Dependencies Calculator
    /// </summary>
    public interface IEFCacheDependenciesProcessor
    {
        /// <summary>
        /// Finds the related table names of the current query.
        /// </summary>
        SortedSet<string> GetCacheDependencies(DbCommand command, DbContext context, EFCachePolicy cachePolicy);

        /// <summary>
        /// Finds the related table names of the current query.
        /// </summary>
        SortedSet<string> GetCacheDependencies(EFCachePolicy cachePolicy, SortedSet<string> tableNames, string commandText);

        /// <summary>
        /// Invalidates all of the cache entries which are dependent on any of the specified root keys.
        /// </summary>
        bool InvalidateCacheDependencies(DbCommand command, DbContext context, EFCachePolicy cachePolicy);

        /// <summary>
        /// Is `insert`, `update` or `delete`?
        /// </summary>
        bool IsCrudCommand(string text);
    }

    /// <summary>
    /// Cache Dependencies Calculator
    /// </summary>
    public class EFCacheDependenciesProcessor : IEFCacheDependenciesProcessor
    {
        private readonly ConcurrentDictionary<Type, Lazy<SortedSet<string>>> _tableNames =
                    new ConcurrentDictionary<Type, Lazy<SortedSet<string>>>();

        private readonly ILogger<EFCacheDependenciesProcessor> _logger;
        private readonly IEFCacheServiceProvider _cacheServiceProvider;
        private readonly EFCoreSecondLevelCacheSettings _cacheSettings;

        /// <summary>
        /// Cache Dependencies Calculator
        /// </summary>
        public EFCacheDependenciesProcessor(
            ILogger<EFCacheDependenciesProcessor> logger,
            IEFCacheServiceProvider cacheServiceProvider,
            IOptions<EFCoreSecondLevelCacheSettings> cacheSettings)
        {
            _logger = logger;
            _cacheServiceProvider = cacheServiceProvider;
            _cacheSettings = cacheSettings.Value;
        }

        /// <summary>
        /// Finds the related table names of the current query.
        /// </summary>
        public SortedSet<string> GetCacheDependencies(DbCommand command, DbContext context, EFCachePolicy cachePolicy)
        {
            var tableNames = getAllTableNames(context);
            return GetCacheDependencies(cachePolicy, tableNames, command.CommandText);
        }

        /// <summary>
        /// Finds the related table names of the current query.
        /// </summary>
        public SortedSet<string> GetCacheDependencies(EFCachePolicy cachePolicy, SortedSet<string> tableNames, string commandText)
        {
            var textsInsideSquareBrackets = getSqlCommandTableNames(commandText);
            var cacheDependencies = new SortedSet<string>(tableNames.Intersect(textsInsideSquareBrackets));
            if (cacheDependencies.Any())
            {
                logProcess(tableNames, textsInsideSquareBrackets, cacheDependencies);
                return cacheDependencies;
            }

            cacheDependencies = cachePolicy.CacheItemsDependencies as SortedSet<string>;
            if (cacheDependencies?.Any() != true)
            {
                if (!_cacheSettings.DisableLogging) _logger.LogDebug($"It's not possible to calculate the related table names of the current query[{commandText}]. Please use EFCachePolicy.Configure(options => options.CacheDependencies(\"real_table_name_1\", \"real_table_name_2\")) to specify them explicitly.");
                cacheDependencies = new SortedSet<string> { EFCachePolicy.EFUnknownsCacheDependency };
            }
            logProcess(tableNames, textsInsideSquareBrackets, cacheDependencies);
            return cacheDependencies;
        }

        private void logProcess(SortedSet<string> tableNames, SortedSet<string> textsInsideSquareBrackets, SortedSet<string> cacheDependencies)
        {
            if (!_cacheSettings.DisableLogging) _logger.LogDebug($"ContextTableNames: {string.Join(", ", tableNames)}, PossibleQueryTableNames: {string.Join(", ", textsInsideSquareBrackets)} -> CacheDependencies: {string.Join(", ", cacheDependencies)}.");
        }

        /// <summary>
        /// Invalidates all of the cache entries which are dependent on any of the specified root keys.
        /// </summary>
        public bool InvalidateCacheDependencies(DbCommand command, DbContext context, EFCachePolicy cachePolicy)
        {
            var commandText = command.CommandText;
            if (!IsCrudCommand(commandText))
            {
                return false;
            }

            var cacheDependencies = GetCacheDependencies(command, context, cachePolicy);
            cacheDependencies.Add(EFCachePolicy.EFUnknownsCacheDependency);
            _cacheServiceProvider.InvalidateCacheDependencies(new EFCacheKey { CacheDependencies = cacheDependencies });

            if (!_cacheSettings.DisableLogging) _logger.LogDebug(CacheableEventId.QueryResultInvalidated, $"Invalidated [{string.Join(", ", cacheDependencies)}] dependencies.");
            return true;
        }

        /// <summary>
        /// Is `insert`, `update` or `delete`?
        /// </summary>
        public bool IsCrudCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string[] crudMarkers = { "insert ", "update ", "delete ", "create " };

            var lines = text.Split('\n');
            foreach (var line in lines)
            {
                foreach (var marker in crudMarkers)
                {
                    if (line.Trim().StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private SortedSet<string> getAllTableNames(DbContext context)
        {
            return _tableNames.GetOrAdd(context.GetType(),
                            _ => new Lazy<SortedSet<string>>(() =>
                            {
                                var tableNames = new SortedSet<string>();
                                foreach (var entityType in context.Model.GetEntityTypes())
                                {
                                    tableNames.Add(entityType.GetTableName());
                                }
                                return tableNames;
                            },
                            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private static SortedSet<string> getSqlCommandTableNames(string commandText)
        {
            string[] tableMarkers = { "FROM", "JOIN", "INTO", "UPDATE" };

            var tables = new SortedSet<string>();

            var sqlItems = commandText.Split(new[] { " ", "\r\n", Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            var sqlItemsLength = sqlItems.Length;
            for (var i = 0; i < sqlItemsLength; i++)
            {
                foreach (var marker in tableMarkers)
                {
                    if (!sqlItems[i].Equals(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ++i;
                    if (i >= sqlItemsLength)
                    {
                        continue;
                    }

                    var tableName = string.Empty;

                    var tableNameParts = sqlItems[i].Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);
                    if (tableNameParts.Length == 1)
                    {
                        tableName = tableNameParts[0].Trim();
                    }
                    else if (tableNameParts.Length >= 2)
                    {
                        tableName = tableNameParts[1].Trim();
                    }

                    if (string.IsNullOrWhiteSpace(tableName))
                    {
                        continue;
                    }

                    tableName = tableName.Replace("[", "")
                                        .Replace("]", "")
                                        .Replace("'", "")
                                        .Replace("`", "")
                                        .Replace("\"", "");
                    tables.Add(tableName);
                }
            }
            return tables;
        }
    }
}