using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DotNetOfficeAzureApp.Services
{
    public class AzureSearchVectorizationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<AzureSearchVectorizationService> _logger;

        public AzureSearchVectorizationService(IConfiguration configuration, ILogger<AzureSearchVectorizationService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        private async Task CreateDataSource(string containerName)
        {
            var dataSourceName = $"vector-{containerName}-datasource";
            var connectionResourceId = _configuration.GetSection("Storage")["connectionResourceId"];

            var dataSourceDefinition = new
            {
                name = dataSourceName,
                description = $"Data source for {containerName} container",
                type = "azureblob",
                credentials = new
                {
                    connectionString = $"ResourceId={connectionResourceId}"
                },
                container = new
                {
                    name = containerName
                }
            };

            string endpoint = _configuration["AISearchServiceEndpoint"];
            string apiKey = _configuration["AISearchApiKey"];

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

            var jsonContent = JsonSerializer.Serialize(dataSourceDefinition);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync(
                $"{endpoint}/datasources/{dataSourceName}?api-version=2024-07-01",
                content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to create data source. Status code: {response.StatusCode}. Error: {error}");
                throw new Exception($"Failed to create data source: {error}");
            }
        }

        public async Task SetupVectorSearch(string containerName)
        {
            try
            {
                await CreateDataSource(containerName);
                await CreateSearchIndex($"vector-{containerName}-index");
                await CreateSkillset(containerName);
                await CreateIndexer(containerName);
                _logger.LogInformation($"Created data source, search index, skillset and indexer for {containerName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up vector search");
                throw;
            }
        }

        private async Task CreateSearchIndex(string indexName)
        {
            var openAiEndpoint = _configuration["AzOpenAIApiBase"];
            var deploymentId = _configuration["AzOpenAIDeploymentId"];
            var modelName = _configuration["AzOpenAIModelName"];

            var indexDefinition = new
            {
                name = indexName,
                fields = new Object[]
                {
                    new { name = "chunk_id", type = "Edm.String", key = true, retrievable = true, stored = true, searchable = true, filterable = false, sortable = true, facetable = false, analyzer = "keyword" },
                    new { name = "parent_id", type = "Edm.String", retrievable = true, stored = true, searchable = false, filterable = true, sortable = false, facetable = false },
                    new { name = "chunk", type = "Edm.String", retrievable = true, stored = true, searchable = true, filterable = false, sortable = false, facetable = false },
                    new { name = "title", type = "Edm.String", retrievable = true, stored = true, searchable = true, filterable = false, sortable = false, facetable = false },
                    new { name = "text_vector", type = "Collection(Edm.Single)", retrievable = true, stored = true, searchable = true, filterable = false, sortable = false, facetable = false, dimensions = 1536, vectorSearchProfile = $"{indexName}-azureOpenAi-text-profile" }
                },
                semantic = new
                {
                    defaultConfiguration = $"{indexName}-semantic-configuration",
                    configurations = new[]
                    {
                        new
                        {
                            name = $"{indexName}-semantic-configuration",
                            prioritizedFields = new
                            {
                                titleField = new { fieldName = "title" },
                                prioritizedContentFields = new[] { new { fieldName = "chunk" } },
                                prioritizedKeywordsFields = new object[] { }
                            }
                        }
                    }
                },
                vectorSearch = new
                {
                    algorithms = new[]
                    {
                        new
                        {
                            name = $"{indexName}-algorithm",
                            kind = "hnsw",
                            hnswParameters = new { m = 4, efConstruction = 400 }
                        }
                    },
                    profiles = new[]
                    {
                        new
                        {
                            name = $"{indexName}-azureOpenAi-text-profile",
                            algorithm = $"{indexName}-algorithm",
                            vectorizer = $"{indexName}-azureOpenAi-text-vectorizer"
                        }
                    },
                    vectorizers = new[]
                    {
                        new
                        {
                            name = $"{indexName}-azureOpenAi-text-vectorizer",
                            kind = "azureOpenAI",
                            azureOpenAIParameters = new
                            {
                                resourceUri = openAiEndpoint,
                                deploymentId = deploymentId,
                                modelName = modelName
                            }
                        }
                    },
                    compressions = new object[] { }
                }
            };

            string endpoint = _configuration["AISearchServiceEndpoint"];
            string apiKey = _configuration["AISearchApiKey"];

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

            var jsonContent = JsonSerializer.Serialize(indexDefinition);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync(
                $"{endpoint}/indexes/{indexName}?api-version=2024-07-01",
                content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to create index. Status code: {response.StatusCode}. Error: {error}");
                throw new Exception($"Failed to create index: {error}");
            }
        }

        private async Task CreateSkillset(string containerName)
        {
            var skillsetName = $"vector-{containerName}-skillset";
            var indexName = $"vector-{containerName}-index";
            var openAiEndpoint = _configuration["AzOpenAIApiBase"];
            var deploymentId = _configuration["AzOpenAIDeploymentId"];
            var modelName = _configuration["AzOpenAIModelName"];

            var skillsetDefinition = new
            {
                name = skillsetName,
                description = "Skillset to chunk documents and generate embeddings",
                skills = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["@odata.type"] = "#Microsoft.Skills.Text.SplitSkill",
                        ["name"] = "#1",
                        ["context"] = "/document",
                        ["inputs"] = new[] { new { name = "text", source = "/document/content" } },
                        ["outputs"] = new[] { new { name = "textItems", targetName = "pages" } },
                        ["textSplitMode"] = "pages",
                        ["maximumPageLength"] = 2000,
                        ["pageOverlapLength"] = 500
                    },
                    new Dictionary<string, object>
                    {
                        ["@odata.type"] = "#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill",
                        ["name"] = "#2",
                        ["context"] = "/document/pages/*",
                        ["inputs"] = new[] { new { name = "text", source = "/document/pages/*" } },
                        ["outputs"] = new[] { new { name = "embedding", targetName = "text_vector" } },
                        ["resourceUri"] = openAiEndpoint,
                        ["deploymentId"] = deploymentId,
                        ["modelName"] = modelName,
                        ["dimensions"] = 1536
                    }
                },
                indexProjections = new
                {
                    selectors = new[]
                    {
                        new
                        {
                            targetIndexName = indexName,
                            parentKeyFieldName = "parent_id",
                            sourceContext = "/document/pages/*",
                            mappings = new[]
                            {
                                new { name = "text_vector", source = "/document/pages/*/text_vector", inputs = new object[] { } },
                                new { name = "chunk", source = "/document/pages/*", inputs = new object[] { } },
                                new { name = "title", source = "/document/title", inputs = new object[] { } }
                            }
                        }
                    },
                    parameters = new { projectionMode = "skipIndexingParentDocuments" }
                }
            };

            string endpoint = _configuration["AISearchServiceEndpoint"];
            string apiKey = _configuration["AISearchApiKey"];

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

            var jsonContent = JsonSerializer.Serialize(skillsetDefinition);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync(
                $"{endpoint}/skillsets/{skillsetName}?api-version=2024-07-01",
                content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to create skillset: {error}");
            }
        }

        private async Task CreateIndexer(string containerName)
        {
            var indexerName = $"vector-{containerName}-indexer";
            var indexName = $"vector-{containerName}-index";
            var dataSourceName = $"vector-{containerName}-datasource";
            var skillsetName = $"vector-{containerName}-skillset";

            string endpoint = _configuration["AISearchServiceEndpoint"];
            string apiKey = _configuration["AISearchApiKey"];

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

            // Check if indexer exists
            var checkResponse = await httpClient.GetAsync($"{endpoint}/indexers/{indexerName}?api-version=2024-07-01");

            if (checkResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Indexer doesn't exist, create new one
                var indexerDefinition = new
                {
                    name = indexerName,
                    description = $"Indexer for {containerName}",
                    dataSourceName = dataSourceName,
                    skillsetName = skillsetName,
                    targetIndexName = indexName,
                    parameters = new
                    {
                        batchSize = 1,
                        maxFailedItems = -1,
                        maxFailedItemsPerBatch = -1,
                        configuration = new
                        {
                            dataToExtract = "contentAndMetadata",
                            parsingMode = "default"
                        }
                    },
                    fieldMappings = new[]
                    {
                new
                {
                    sourceFieldName = "metadata_storage_name",
                    targetFieldName = "title"
                },
                new
                {
                    sourceFieldName = "content",
                    targetFieldName = "chunk"
                }
            }
                };

                var jsonContent = JsonSerializer.Serialize(indexerDefinition);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var createResponse = await httpClient.PutAsync(
                    $"{endpoint}/indexers/{indexerName}?api-version=2024-07-01",
                    content);

                if (!createResponse.IsSuccessStatusCode)
                {
                    var error = await createResponse.Content.ReadAsStringAsync();
                    throw new Exception($"Failed to create indexer: {error}");
                }

                _logger.LogInformation($"Created new indexer: {indexerName}");
            }
            else
            {
                _logger.LogInformation($"Using existing indexer: {indexerName}");
            }

            // Run the indexer
            var runResponse = await httpClient.PostAsync(
                $"{endpoint}/indexers/{indexerName}/run?api-version=2024-07-01",
                null);

            if (!runResponse.IsSuccessStatusCode)
            {
                var error = await runResponse.Content.ReadAsStringAsync();
                _logger.LogWarning($"Warning running indexer: {error}");
            }
            else
            {
                _logger.LogInformation($"Successfully ran indexer: {indexerName}");
            }
        }

        public async Task RunExistingIndexer(string containerName)
        {
            try
            {
                string indexerName = $"vector-{containerName}-indexer";
                string endpoint = _configuration["AISearchServiceEndpoint"];
                string apiKey = _configuration["AISearchApiKey"];

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                // Run the existing indexer
                var response = await httpClient.PostAsync(
                    $"{endpoint}/indexers/{indexerName}/run?api-version=2024-07-01",
                    null);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning($"Warning running existing indexer: {error}");
                }
                else
                {
                    _logger.LogInformation($"Successfully ran existing indexer for container: {containerName}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error running existing indexer for container: {containerName}");
                throw;
            }
        }
    }
}