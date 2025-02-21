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

        private HttpClient CreateHttpClientForPrivateEndpoint()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMinutes(5); // Increase timeout for potentially slow private network operations
            return client;
        }

        private async Task CreateDataSource(string containerName)
        {
            try
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

                using var httpClient = CreateHttpClientForPrivateEndpoint();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                var jsonContent = JsonSerializer.Serialize(dataSourceDefinition);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                _logger.LogInformation($"Creating data source: {dataSourceName} for container: {containerName}");
                var response = await httpClient.PutAsync(
                    $"{endpoint}/datasources/{dataSourceName}?api-version=2024-07-01",
                    content);

                var responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to create data source. Status code: {response.StatusCode}. Error: {responseContent}");
                    throw new Exception($"Failed to create data source: {responseContent}");
                }

                _logger.LogInformation($"Successfully created data source: {dataSourceName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in CreateDataSource for container {containerName}");
                throw;
            }
        }

        public async Task SetupVectorSearch(string containerName)
        {
            try
            {
                _logger.LogInformation($"Setting up vector search for container: {containerName}");
                await CreateDataSource(containerName);
                await CreateSearchIndex($"vector-{containerName}-index");
                await CreateSkillset(containerName);
                await CreateIndexer(containerName);
                _logger.LogInformation($"Successfully created data source, search index, skillset and indexer for {containerName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error setting up vector search for container: {containerName}");
                throw;
            }
        }

        private async Task CreateSearchIndex(string indexName)
        {
            try
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

                using var httpClient = CreateHttpClientForPrivateEndpoint();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                var jsonContent = JsonSerializer.Serialize(indexDefinition);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                _logger.LogInformation($"Creating search index: {indexName}");
                var response = await httpClient.PutAsync(
                    $"{endpoint}/indexes/{indexName}?api-version=2024-07-01",
                    content);

                var responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to create index. Status code: {response.StatusCode}. Error: {responseContent}");
                    throw new Exception($"Failed to create index: {responseContent}");
                }

                _logger.LogInformation($"Successfully created search index: {indexName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating search index");
                throw;
            }
        }

        private async Task CreateSkillset(string containerName)
        {
            try
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

                using var httpClient = CreateHttpClientForPrivateEndpoint();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                var jsonContent = JsonSerializer.Serialize(skillsetDefinition);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                _logger.LogInformation($"Creating skillset: {skillsetName}");
                var response = await httpClient.PutAsync(
                    $"{endpoint}/skillsets/{skillsetName}?api-version=2024-07-01",
                    content);

                var responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to create skillset. Status code: {response.StatusCode}. Error: {responseContent}");
                    throw new Exception($"Failed to create skillset: {responseContent}");
                }

                _logger.LogInformation($"Successfully created skillset: {skillsetName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating skillset");
                throw;
            }
        }

        private async Task CreateIndexer(string containerName)
        {
            try
            {
                var indexerName = $"vector-{containerName}-indexer";
                var indexName = $"vector-{containerName}-index";
                var dataSourceName = $"vector-{containerName}-datasource";
                var skillsetName = $"vector-{containerName}-skillset";

                string endpoint = _configuration["AISearchServiceEndpoint"];
                string apiKey = _configuration["AISearchApiKey"];

                using var httpClient = CreateHttpClientForPrivateEndpoint();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                // Check if indexer exists
                _logger.LogInformation($"Checking if indexer {indexerName} exists");
                var checkResponse = await httpClient.GetAsync($"{endpoint}/indexers/{indexerName}?api-version=2024-07-01");

                if (checkResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Indexer doesn't exist, create new one
                    _logger.LogInformation($"Indexer {indexerName} not found, creating new one");

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

                    var responseContent = await createResponse.Content.ReadAsStringAsync();
                    if (!createResponse.IsSuccessStatusCode)
                    {
                        _logger.LogError($"Failed to create indexer. Status code: {createResponse.StatusCode}. Error: {responseContent}");
                        throw new Exception($"Failed to create indexer: {responseContent}");
                    }

                    _logger.LogInformation($"Created new indexer: {indexerName}");
                }
                else
                {
                    _logger.LogInformation($"Indexer {indexerName} already exists");
                }

                // Run the indexer
                _logger.LogInformation($"Running indexer: {indexerName}");
                var runResponse = await httpClient.PostAsync(
                    $"{endpoint}/indexers/{indexerName}/run?api-version=2024-07-01",
                    null);

                var runResponseContent = await runResponse.Content.ReadAsStringAsync();
                if (!runResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Warning running indexer: Status code: {runResponse.StatusCode}. Response: {runResponseContent}");
                }
                else
                {
                    _logger.LogInformation($"Successfully started indexer: {indexerName}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in CreateIndexer for container: {containerName}");
                throw;
            }
        }

        public async Task RunExistingIndexer(string containerName)
        {
            try
            {
                string indexerName = $"vector-{containerName}-indexer";
                string endpoint = _configuration["AISearchServiceEndpoint"];
                string apiKey = _configuration["AISearchApiKey"];

                using var httpClient = CreateHttpClientForPrivateEndpoint();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                _logger.LogInformation($"Running existing indexer: {indexerName}");
                // Run the existing indexer
                var response = await httpClient.PostAsync(
                    $"{endpoint}/indexers/{indexerName}/run?api-version=2024-07-01",
                    null);

                var responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Warning running existing indexer: Status code: {response.StatusCode}. Response: {responseContent}");
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

        public async Task<string> GetIndexerStatus(string containerName)
        {
            try
            {
                string indexerName = $"vector-{containerName}-indexer";
                string endpoint = _configuration["AISearchServiceEndpoint"];
                string apiKey = _configuration["AISearchApiKey"];

                using var httpClient = CreateHttpClientForPrivateEndpoint();
                httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                _logger.LogInformation($"Checking status of indexer: {indexerName}");
                var response = await httpClient.GetAsync(
                    $"{endpoint}/indexers/{indexerName}/status?api-version=2024-07-01");

                var responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to get indexer status. Status code: {response.StatusCode}. Error: {responseContent}");
                    return $"Error: {response.StatusCode}";
                }

                return responseContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting indexer status for container: {containerName}");
                return $"Exception: {ex.Message}";
            }
        }
    }
}