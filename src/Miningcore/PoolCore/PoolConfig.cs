/*
MiningCore 2.0
Copyright 2021 MinerNL (Miningcore.com)
*/

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;


namespace Miningcore.PoolCore
{
    public class PoolConfig
    {
        private const string BaseConfigFile = "config.json";
        private static ClusterConfig clusterConfig;
        private static readonly Regex RegexJsonTypeConversionError = new Regex("\"([^\"]+)\"[^\']+\'([^\']+)\'.+\\s(\\d+),.+\\s(\\d+)", RegexOptions.Compiled);

        public static ClusterConfig GetConfigContent(string configFile)
        {
            // Read config.json file
            clusterConfig = ReadConfig(configFile);
            ValidateConfig();

            return clusterConfig;
        }

        public static ClusterConfig GetConfigContentFromJson(string config)
        {
            try
            {
                var baseConfig = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(BaseConfigFile));
                var toBeMerged = JObject.Parse(config);
                baseConfig.Merge(toBeMerged, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Merge });
                clusterConfig = baseConfig.ToObject<ClusterConfig>();
            }
            catch(JsonSerializationException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
            catch(JsonException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
            catch(IOException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }

            ValidateConfig();

            return clusterConfig;
        }
        
        public static ClusterConfig GetConfigContentFromAppConfig(string prefix)
        {
            // Read config.json file
            clusterConfig = ReadConfigFromAppConfiguration(prefix);
            ValidateConfig();

            return clusterConfig;

        }

        private static ClusterConfig ReadConfigFromAppConfiguration(string prefix)
        {
            try
            {
                var config = AzureAppConfiguration.GetAppConfig(prefix);

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                });

                var reader = new JsonTextReader(new StringReader(config[AzureAppConfiguration.ConfigJson]));

                clusterConfig = serializer.Deserialize<ClusterConfig>(reader);
                // Update dynamic pass and others config here

                clusterConfig.Persistence.Postgres.User = config[AzureAppConfiguration.PersistencePostgresUser];
                clusterConfig.Persistence.Postgres.Password = config[AzureAppConfiguration.PersistencePostgresPassword];
                foreach(var poolConfig in clusterConfig.Pools)
                {
                    poolConfig.PaymentProcessing.Extra["coinbasePassword"] = config["pools." + poolConfig.Id + "." + AzureAppConfiguration.CoinbasePassword];
                }

                return clusterConfig;

            }
            catch(JsonSerializationException ex)
            {
                HumanizeJsonParseException(ex);
                throw;
            }

            catch(JsonException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        private static ClusterConfig ReadConfig(string configFile)
        {
            try
            {
                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                });

                using(var reader = new StreamReader(configFile, Encoding.UTF8))
                {
                    using(var jsonReader = new JsonTextReader(reader))
                    {
                        return serializer.Deserialize<ClusterConfig>(jsonReader);
                    }
                }
            }
            catch(JsonSerializationException ex)
            {
                HumanizeJsonParseException(ex);
                throw;
            }
            catch(JsonException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
            catch(IOException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        private static void ValidateConfig()
        {
            // set some defaults
            foreach(var config in clusterConfig.Pools)
            {
                if(!config.EnableInternalStratum.HasValue)
                    config.EnableInternalStratum = clusterConfig.ShareRelays == null || clusterConfig.ShareRelays.Length == 0;
            }
            try
            {
                clusterConfig.Validate();
            }
            catch(ValidationException ex)
            {
                Console.WriteLine($"Configuration is not valid:\n\n{string.Join("\n", ex.Errors.Select(x => "=> " + x.ErrorMessage))}");
                throw new PoolStartupAbortException(string.Empty);
            }
            finally
            {
                Console.WriteLine($"Pool Configuration file is valid");
            }
        }

        private static void HumanizeJsonParseException(JsonSerializationException ex)
        {
            var m = RegexJsonTypeConversionError.Match(ex.Message);

            if(m.Success)
            {
                var value = m.Groups[1].Value;
                var type = Type.GetType(m.Groups[2].Value);
                var line = m.Groups[3].Value;
                var col = m.Groups[4].Value;

                if(type == typeof(PayoutScheme))
                    Console.WriteLine($"Error: Payout scheme '{value}' is not (yet) supported (line {line}, column {col})");
            }

            else
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        public static void DumpParsedConfig(ClusterConfig config)
        {
            Console.WriteLine("\nCurrent configuration as parsed from config file:");

            Console.WriteLine(JsonConvert.SerializeObject(config, new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.Indented
            }));
        }
    }
}
