using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.AI.OpenAI;
using System.Reflection;
using Azure.Search.Documents;
using System.Text.Json;
using Azure.Storage.Blobs;
using DotNetOfficeAzureApp.Services;
using DotNetOfficeAzureApp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetOfficeAzureApp.Services
{
    public class AzureAISearchService : IAzureAISearchService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<AzureAISearchService> _logger;
        private readonly SearchIndexerClient _indexerClient;
        private readonly SearchIndexClient _indexClient;
        private readonly string _azureOpenAIApiBase;
        private readonly string _azureOpenAIKey;
        private readonly string _azuresearchServiceEndpoint;
        private readonly string _azuresearchApiKey;
        private readonly string _azureOpenAIDeploymentId;
        private readonly IServiceProvider _serviceProvider;
        private readonly OpenAIClient _client;
        private ChatCompletionsOptions _options;

        private class ChatSettings
        {
            public int MaxTokens { get; set; } = 800;
            public float Temperature { get; set; } = 0.7f;
            public float TopP { get; set; } = 0.95f;
            public float FrequencyPenalty { get; set; } = 0;
            public float PresencePenalty { get; set; } = 0;
        }

        private readonly ChatSettings _chatSettings = new ChatSettings();

        public AzureAISearchService(IConfiguration configuration, ILogger<AzureAISearchService> logger, IServiceProvider serviceProvider)
        {
            _configuration = configuration;
            _logger = logger;
            _serviceProvider = serviceProvider;

            _azuresearchServiceEndpoint = _configuration.GetValue<string>("AISearchServiceEndpoint");
            _azuresearchApiKey = _configuration.GetValue<string>("AISearchApiKey");
            _azureOpenAIApiBase = _configuration.GetValue<string>("AzOpenAIApiBase");
            _azureOpenAIKey = _configuration.GetValue<string>("AzOpenAIKey");
            _azureOpenAIDeploymentId = _configuration.GetValue<string>("AzOpenAIDeploymentId");

            // Configure client options for private endpoints
            var clientOptions = new OpenAIClientOptions
            {
                Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }))
            };

            // Configure search client options
            var searchClientOptions = new SearchClientOptions
            {
                Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }))
            };

            _client = new OpenAIClient(new Uri(_azureOpenAIApiBase), new AzureKeyCredential(_azureOpenAIKey), clientOptions);
            _indexerClient = new SearchIndexerClient(new Uri(_azuresearchServiceEndpoint),
                new AzureKeyCredential(_azuresearchApiKey), searchClientOptions);
            _indexClient = new SearchIndexClient(new Uri(_azuresearchServiceEndpoint),
                new AzureKeyCredential(_azuresearchApiKey), searchClientOptions);

            CreateChatCompletionOptions();
        }

        public bool UpdateIndexer()
        {
            try
            {
                return UpdateIndexerForContainer("general").Result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating indexer");
                return false;
            }
        }

        public async Task<bool> UpdateIndexerForContainer(string containerName)
        {
            try
            {
                bool setupSuccess = await CreateAndConfigureDataSourceAndIndexer(containerName);
                if (!setupSuccess)
                {
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating indexer for container {containerName}");
                return false;
            }
        }

        private async Task<bool> CreateAndConfigureDataSourceAndIndexer(string containerName)
        {
            try
            {
                string dataSourceName = $"azure-blob-{containerName}-datasource";
                string indexerName = $"vector-{containerName}-indexer";
                string indexName = $"vector-{containerName}-index";
                string connectionString = _configuration.GetSection("Storage")["connectionString"];

                // Create data source
                try
                {
                    var dataSource = new SearchIndexerDataSourceConnection(
                        dataSourceName,
                        SearchIndexerDataSourceType.AzureBlob,
                        connectionString,
                        new SearchIndexerDataContainer(containerName))
                    {
                        Description = $"Blob data source for {containerName} container"
                    };

                    await _indexerClient.CreateOrUpdateDataSourceConnectionAsync(dataSource);
                    _logger.LogInformation($"Data source {dataSourceName} created successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error creating data source for {containerName}");
                    return false;
                }

                // Create indexer
                try
                {
                    var indexerParameters = new IndexingParameters
                    {
                        MaxFailedItems = -1,
                        MaxFailedItemsPerBatch = -1
                    };

                    indexerParameters.Configuration.Add("dataToExtract", "contentAndMetadata");
                    indexerParameters.Configuration.Add("parsingMode", "default");

                    var indexer = new SearchIndexer(
                        indexerName,
                        dataSourceName,
                        indexName)
                    {
                        Description = $"Indexer for {containerName}",
                        Schedule = new IndexingSchedule(TimeSpan.FromMinutes(5)),
                        Parameters = indexerParameters
                    };

                    indexer.FieldMappings.Add(new FieldMapping("metadata_storage_name")
                    { TargetFieldName = "title" });
                    indexer.FieldMappings.Add(new FieldMapping("content")
                    { TargetFieldName = "chunk" });

                    // Try to delete existing indexer first
                    try
                    {
                        await _indexerClient.DeleteIndexerAsync(indexerName);
                        await Task.Delay(2000); // Wait for deletion to complete
                    }
                    catch { } // Ignore if it doesn't exist

                    await _indexerClient.CreateIndexerAsync(indexer);
                    _logger.LogInformation($"Indexer {indexerName} created successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error creating indexer for {containerName}");
                    return false;
                }

                // Create search index
                try
                {
                    var fields = new List<SearchField>
                    {
                        new SearchField("id", SearchFieldDataType.String) { IsKey = true },
                        new SearchField("content", SearchFieldDataType.String)
                        {
                            IsSearchable = true,
                            IsFilterable = true,
                            AnalyzerName = "standard.lucene"
                        },
                        new SearchField("title", SearchFieldDataType.String)
                        {
                            IsSearchable = true,
                            IsFilterable = true,
                            AnalyzerName = "standard.lucene"
                        },
                        new SearchField("chunk", SearchFieldDataType.String)
                        {
                            IsSearchable = true,
                            IsFilterable = true
                        }
                    };

                    var index = new SearchIndex(indexName, fields);
                    await _indexClient.CreateOrUpdateIndexAsync(index);
                    _logger.LogInformation($"Search index {indexName} created successfully");

                    // Run the indexer
                    await _indexerClient.RunIndexerAsync(indexerName);
                    _logger.LogInformation($"Indexer {indexerName} started running");

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating search index");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in CreateAndConfigureDataSourceAndIndexer for {containerName}");
                return false;
            }
        }

        public async Task<Response<ChatCompletions>> SearchResultByOpenAI(string chatInput, string containerName = "general")
        {
            try
            {
                var indexName = $"vector-{containerName}-index";
                _logger.LogInformation($"Searching in index: {indexName}");

                // Configure client with certificate validation for private endpoints
                var clientOptions = new OpenAIClientOptions
                {
                    Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    }))
                };

                var client = new OpenAIClient(
                    new Uri(_azureOpenAIApiBase),
                    new AzureKeyCredential(_azureOpenAIKey),
                    clientOptions);

                var options = new ChatCompletionsOptions();

                // Set properties that are common across versions
                options.Temperature = _chatSettings.Temperature;
                options.MaxTokens = _chatSettings.MaxTokens;
                options.FrequencyPenalty = _chatSettings.FrequencyPenalty;
                options.PresencePenalty = _chatSettings.PresencePenalty;

                // Add messages
                options.Messages.Add(new ChatMessage(ChatRole.System,
                    "You are a helpful assistant. Provide clear and informative responses based on the available documents."));
                options.Messages.Add(new ChatMessage(ChatRole.User, chatInput));

                // Set Azure extensions
                options.AzureExtensionsOptions = new AzureChatExtensionsOptions();

                // Create the search extension
                var searchExtension = new AzureCognitiveSearchChatExtensionConfiguration();

                // Set properties using direct assignment where possible
                searchExtension.SearchEndpoint = new Uri(_azuresearchServiceEndpoint);
                searchExtension.IndexName = indexName;

                // For the API key, we need to use the property setter method
                Type extensionType = searchExtension.GetType();
                extensionType.GetMethod("set_SearchKey")?.Invoke(
                    searchExtension,
                    new object[] { new AzureKeyCredential(_azuresearchApiKey) });

                options.AzureExtensionsOptions.Extensions.Add(searchExtension);

                string chatDeploymentId = _configuration.GetValue<string>("AzOpenAIChatDeploymentId") ?? "gpt-4o";

                // For this version, you need to set the deployment name on the options
                options.DeploymentName = chatDeploymentId;

                // Call method without deployment parameter
                return await client.GetChatCompletionsAsync(options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SearchResultByOpenAI. Container: {Container}, Query: {Query}",
                    containerName, chatInput);
                throw;
            }
        }

        public async Task<(string Content, List<CitationSourceInfo> Citations)> SearchResultByOpenAIWithFullCitations(string chatInput, string containerName = "general", string userEmail = null)
        {
            try
            {
                if (string.IsNullOrEmpty(userEmail))
                {
                    _logger.LogWarning("No user email provided for access control check");
                    return ("I'm sorry, but you need to be logged in to search documents.", new List<CitationSourceInfo>());
                }

                // Get accessible documents for this user from your AccessControlService
                var accessControlService = _serviceProvider.GetRequiredService<IAccessControlService>();
                var accessibleDocuments = await GetAccessibleDocumentsForUser(containerName, userEmail, accessControlService);

                _logger.LogInformation("User {UserEmail} has access to {Count} documents in container {Container}",
                    userEmail, accessibleDocuments.Count, containerName);

                if (accessibleDocuments.Count == 0)
                {
                    return ("I'm sorry, but you don't have access to any documents in this container.", new List<CitationSourceInfo>());
                }

                var indexName = $"vector-{containerName}-index";
                _logger.LogInformation($"Searching in index: {indexName}");

                // Configure client with certificate validation for private endpoints
                var clientOptions = new OpenAIClientOptions
                {
                    Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    }))
                };

                var client = new OpenAIClient(
                    new Uri(_azureOpenAIApiBase),
                    new AzureKeyCredential(_azureOpenAIKey),
                    clientOptions);

                var options = new ChatCompletionsOptions();

                // Set properties that are common across versions
                options.Temperature = _chatSettings.Temperature;
                options.MaxTokens = _chatSettings.MaxTokens;
                options.FrequencyPenalty = _chatSettings.FrequencyPenalty;
                options.PresencePenalty = _chatSettings.PresencePenalty;

                // Add messages with specific instructions for citation formatting and content structure
                options.Messages.Add(new ChatMessage(ChatRole.System,
                    @"You are a helpful assistant. Provide clear, well-structured, and informative responses based on the available documents.
                    
                    IMPORTANT: You can only access documents that the user has permission to view. Do not reference or provide information from documents that are not accessible to the current user.
                    
                    IMPORTANT FORMATTING INSTRUCTIONS:
                    1. Structure your response with proper Markdown formatting:
                       - Use bold (**text**) for important terms or subheadings
                       - Use bullet points (* ) for lists
                       - Use proper spacing between sections
                       
                    2. Citation formatting:
                       - Use the exact format '[doc1]', '[doc2]' for citations
                       - Place citations INLINE after sentences or relevant information
                       - Each citation should be appropriate to the information it references
                       
                    3. Content organization:
                       - Begin with a concise summary or introduction
                       - Organize information into logical sections
                       - Use bullet points for lists of features, requirements, etc.
                       - All citation references go in the main content
                       
                    4. IMPORTANT: Do not include a references or citations section in your response. 
                       Put all citations inline with the content. The references will be displayed separately below your answer."));

                options.Messages.Add(new ChatMessage(ChatRole.User, chatInput));

                // Set Azure extensions
                options.AzureExtensionsOptions = new AzureChatExtensionsOptions();

                // Create the search extension
                var searchExtension = new AzureCognitiveSearchChatExtensionConfiguration();

                // Set properties
                searchExtension.SearchEndpoint = new Uri(_azuresearchServiceEndpoint);
                searchExtension.IndexName = indexName;

                // Try to set filter to only include accessible documents
                if (accessibleDocuments.Count > 0)
                {
                    try
                    {
                        // Create filter condition for document titles
                        var titleFilterConditions = accessibleDocuments
                            .Select(doc => $"search.in(title, '{EscapeFilterValue(doc)}')")
                            .ToList();

                        // Join conditions with OR
                        var filterExpression = string.Join(" or ", titleFilterConditions);

                        // Set the filter property using reflection if available
                        var filterProperty = searchExtension.GetType().GetProperty("Filter");
                        if (filterProperty != null)
                        {
                            filterProperty.SetValue(searchExtension, filterExpression);
                            _logger.LogInformation("Set document filter: {Filter}", filterExpression);
                        }
                        else
                        {
                            _logger.LogWarning("Could not set document filter - Filter property not found");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error setting document filter");
                    }
                }

                // For the API key, we need to use the property setter method
                Type extensionType = searchExtension.GetType();
                extensionType.GetMethod("set_SearchKey")?.Invoke(
                    searchExtension,
                    new object[] { new AzureKeyCredential(_azuresearchApiKey) });

                options.AzureExtensionsOptions.Extensions.Add(searchExtension);

                string chatDeploymentId = _configuration.GetValue<string>("AzOpenAIChatDeploymentId") ?? "gpt-4o";

                // For this version, you need to set the deployment name on the options
                options.DeploymentName = chatDeploymentId;

                // Call method without deployment parameter
                var response = await client.GetChatCompletionsAsync(options);

                if (response?.Value?.Choices != null && response.Value.Choices.Count > 0)
                {
                    string answer = response.Value.Choices[0].Message.Content;

                    // Extract citation markers ([doc1], [doc2], etc.)
                    var citationMatches = System.Text.RegularExpressions.Regex.Matches(answer, @"\[doc(\d+)\]");
                    var citationNumbers = new HashSet<int>();

                    foreach (System.Text.RegularExpressions.Match match in citationMatches)
                    {
                        if (int.TryParse(match.Groups[1].Value, out int citationNumber))
                        {
                            citationNumbers.Add(citationNumber);
                        }
                    }

                    var citations = new List<CitationSourceInfo>();
                    Dictionary<int, (string source, string content)> contextCitations = new Dictionary<int, (string, string)>();

                    // Get the message for exploring context and tool responses
                    var messageChoice = response.Value.Choices[0].Message;

                    // Helper method to extract property values from objects
                    string ExtractPropertyValue(object obj, params string[] propertyNames)
                    {
                        if (obj == null) return null;

                        foreach (var propName in propertyNames)
                        {
                            try
                            {
                                var property = obj.GetType().GetProperty(propName,
                                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                                if (property != null)
                                {
                                    var value = property.GetValue(obj)?.ToString();
                                    if (!string.IsNullOrEmpty(value))
                                    {
                                        return value;
                                    }
                                }
                            }
                            catch
                            {
                                // Continue with next property name
                            }
                        }

                        return null;
                    }

                    // Try to access extensions (newer API versions)
                    var extensionsProperty = messageChoice.GetType().GetProperty("Extensions");
                    var toolMessages = extensionsProperty?.GetValue(messageChoice);

                    if (toolMessages != null)
                    {
                        _logger.LogInformation($"Found Extensions: {toolMessages.GetType().Name}");

                        // Try to find citations in extensions
                        var citationProperty = toolMessages.GetType().GetProperty("Citations") ??
                                              toolMessages.GetType().GetProperty("citations") ??
                                              toolMessages.GetType().GetProperty("Messages") ??
                                              toolMessages.GetType().GetProperty("messages") ??
                                              toolMessages.GetType().GetProperty("tool_responses") ??
                                              toolMessages.GetType().GetProperty("data_sources");

                        if (citationProperty != null)
                        {
                            var citationData = citationProperty.GetValue(toolMessages);
                            _logger.LogInformation($"Found citation data: {citationData?.GetType().Name ?? "null"}");

                            if (citationData is System.Collections.IEnumerable citationEnum)
                            {
                                int index = 0;
                                foreach (var item in citationEnum)
                                {
                                    index++;
                                    if (item != null)
                                    {
                                        _logger.LogInformation($"Citation item {index} type: {item.GetType().Name}");

                                        // Try different property names that might contain document info
                                        string docName = ExtractPropertyValue(item, "title", "name", "source", "fileName") ?? $"Document_{index}.pdf";
                                        string docContent = ExtractPropertyValue(item, "content", "text", "chunk", "value") ?? "Content not available";

                                        // Try getting citation index
                                        int citationIndex = index;
                                        var indexProperty = item.GetType().GetProperty("Index") ??
                                                           item.GetType().GetProperty("index") ??
                                                           item.GetType().GetProperty("id");

                                        if (indexProperty != null)
                                        {
                                            var indexValue = indexProperty.GetValue(item)?.ToString();
                                            if (!string.IsNullOrEmpty(indexValue))
                                            {
                                                if (indexValue.StartsWith("doc") && int.TryParse(indexValue.Substring(3), out int idxVal1))
                                                {
                                                    citationIndex = idxVal1;
                                                }
                                                else if (int.TryParse(indexValue, out int idxVal2))
                                                {
                                                    citationIndex = idxVal2;
                                                }
                                            }
                                        }

                                        // Cleanup title if it's a file path
                                        if (!string.IsNullOrEmpty(docName) && (docName.Contains("/") || docName.Contains("\\")))
                                        {
                                            docName = System.IO.Path.GetFileName(docName);
                                        }

                                        // Only process citations that are referenced in the text
                                        if (citationNumbers.Contains(citationIndex))
                                        {
                                            if (!string.IsNullOrEmpty(docName))
                                            {
                                                contextCitations[citationIndex] = (docName, docContent);
                                            }

                                            if (!string.IsNullOrEmpty(docName) && !string.IsNullOrEmpty(docContent))
                                            {
                                                citations.Add(new CitationSourceInfo
                                                {
                                                    Source = docName,
                                                    Content = docContent,
                                                    Index = citationIndex
                                                });

                                                _logger.LogInformation("Added citation {Index}: {Title}", citationIndex, docName);
                                            }
                                        }
                                    }
                                }
                            }
                            else if (citationData is System.Text.Json.JsonElement jsonElement)
                            {
                                // Process JSON format citations
                                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    int index = 0;
                                    foreach (var item in jsonElement.EnumerateArray())
                                    {
                                        index++;
                                        try
                                        {
                                            string docName = "Unknown";
                                            string docContent = "Content not available";
                                            int citationIndex = index;

                                            // Try to extract properties
                                            if (item.TryGetProperty("title", out var titleProp))
                                                docName = titleProp.GetString() ?? docName;
                                            else if (item.TryGetProperty("name", out titleProp))
                                                docName = titleProp.GetString() ?? docName;
                                            else if (item.TryGetProperty("source", out titleProp))
                                                docName = titleProp.GetString() ?? docName;

                                            if (item.TryGetProperty("content", out var contentProp))
                                                docContent = contentProp.GetString() ?? docContent;
                                            else if (item.TryGetProperty("text", out contentProp))
                                                docContent = contentProp.GetString() ?? docContent;
                                            else if (item.TryGetProperty("chunk", out contentProp))
                                                docContent = contentProp.GetString() ?? docContent;

                                            // Try get citation index
                                            if (item.TryGetProperty("index", out var indexProp))
                                            {
                                                var indexStr = indexProp.GetString();
                                                if (!string.IsNullOrEmpty(indexStr))
                                                {
                                                    if (indexStr.StartsWith("doc") && int.TryParse(indexStr.Substring(3), out int idxVal3))
                                                        citationIndex = idxVal3;
                                                    else if (int.TryParse(indexStr, out int idxVal4))
                                                        citationIndex = idxVal4;
                                                }
                                            }

                                            // Only process citations that are referenced in the text
                                            if (citationNumbers.Contains(citationIndex))
                                            {
                                                if (!string.IsNullOrEmpty(docName))
                                                {
                                                    contextCitations[citationIndex] = (docName, docContent);
                                                }

                                                citations.Add(new CitationSourceInfo
                                                {
                                                    Source = docName,
                                                    Content = docContent,
                                                    Index = citationIndex
                                                });
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogWarning(ex, $"Error processing JSON citation at index {index}");
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Try to access Context property (older API versions)
                    if (citations.Count == 0)
                    {
                        var contextProperty = messageChoice.GetType().GetProperty("Context") ??
                                              messageChoice.GetType().GetProperty("ContextData");

                        if (contextProperty != null)
                        {
                            var contextData = contextProperty.GetValue(messageChoice);

                            if (contextData != null)
                            {
                                _logger.LogInformation($"Found Context: {contextData.GetType().Name}");

                                // Log properties to help debug
                                foreach (var prop in contextData.GetType().GetProperties())
                                {
                                    _logger.LogInformation($"Context property: {prop.Name} ({prop.PropertyType.Name})");
                                }

                                // Try to find citations in context
                                var dataSourcesProperty = FindProperty(contextData, "Citations", "citations",
                                    "dataSources", "data_sources", "sources");

                                if (dataSourcesProperty != null)
                                {
                                    var dataSources = dataSourcesProperty.GetValue(contextData);

                                    if (dataSources != null)
                                    {
                                        _logger.LogInformation($"Found data sources: {dataSources.GetType().Name}");

                                        if (dataSources is System.Collections.IEnumerable sourceEnum)
                                        {
                                            int index = 0;
                                            foreach (var item in sourceEnum)
                                            {
                                                index++;
                                                if (item != null)
                                                {
                                                    string docName = ExtractPropertyValue(item, "title", "name", "source") ?? $"Document_{index}.pdf";
                                                    string docContent = ExtractPropertyValue(item, "content", "text", "chunk") ?? "Content not available";

                                                    contextCitations[index] = (docName, docContent);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Create citations based on the citation numbers found in the text
                    foreach (var citationNumber in citationNumbers)
                    {
                        // Try to get citation from context data first
                        if (contextCitations.TryGetValue(citationNumber, out var citation))
                        {
                            citations.Add(new CitationSourceInfo
                            {
                                Source = citation.source,
                                Content = citation.content,
                                Index = citationNumber
                            });
                            continue;
                        }

                        // If not in context, try search index as a fallback
                        try
                        {
                            // Use direct Azure Cognitive Search to get document info
                            var searchClient = new SearchClient(
                                new Uri(_azuresearchServiceEndpoint),
                                indexName,
                                new AzureKeyCredential(_azuresearchApiKey));

                            // Search for documents related to the query
                            var searchOptions = new SearchOptions
                            {
                                Size = Math.Max(10, citationNumber + 2), // Get enough results to cover all citation numbers
                                IncludeTotalCount = true,
                                Select = { "title", "chunk" }
                            };

                            // Search for documents that might match this citation
                            var searchResults = await searchClient.SearchAsync<Dictionary<string, object>>(chatInput, searchOptions);

                            if (searchResults.Value.TotalCount > 0)
                            {
                                // Get all the documents
                                var documents = searchResults.Value.GetResults().ToList();

                                // Try to get the document with the citation's index
                                var targetIndex = citationNumber - 1;
                                if (targetIndex >= 0 && targetIndex < documents.Count)
                                {
                                    var doc = documents[targetIndex].Document;

                                    string title = "Unknown";
                                    string content = "Content not available";

                                    if (doc.ContainsKey("title"))
                                        title = doc["title"]?.ToString() ?? title;

                                    if (doc.ContainsKey("chunk"))
                                        content = doc["chunk"]?.ToString() ?? content;

                                    citations.Add(new CitationSourceInfo
                                    {
                                        Source = title,
                                        Content = content,
                                        Index = citationNumber
                                    });

                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error searching for citation {citationNumber}");
                        }

                        // If we still don't have a citation, add a placeholder
                        if (!citations.Any(c => c.Index == citationNumber))
                        {
                            citations.Add(new CitationSourceInfo
                            {
                                Source = $"Document_{citationNumber}.pdf",
                                Content = $"Document reference could not be retrieved.",
                                Index = citationNumber
                            });
                        }
                    }

                    // Check if we have at least one citation with real content
                    bool hasRealContent = citations.Any(c =>
                        !c.Content.Contains("Document reference could not be retrieved") &&
                        !c.Content.Contains("Content not available"));

                    // If we have at least one real citation, remove the placeholder ones
                    if (hasRealContent)
                    {
                        citations = citations.Where(c =>
                            !c.Content.Contains("Document reference could not be retrieved") &&
                            !c.Content.Contains("Content not available")).ToList();
                    }

                    // Filter citations to only include documents the user has access to
                    var filteredCitations = citations.Where(c =>
                        accessibleDocuments.Any(doc =>
                            doc.Equals(c.Source, StringComparison.OrdinalIgnoreCase) ||
                            System.IO.Path.GetFileName(doc).Equals(System.IO.Path.GetFileName(c.Source), StringComparison.OrdinalIgnoreCase)
                        )
                    ).ToList();

                    // If we filtered out all citations, but still have some in the original list, 
                    // this means user doesn't have access to the cited documents
                    if (filteredCitations.Count == 0 && citations.Count > 0)
                    {
                        _logger.LogWarning("User {UserEmail} does not have access to any of the cited documents", userEmail);

                        // Return a message indicating the issue
                        return ("I found information related to your query, but you don't have access to the source documents. Please contact your administrator if you need access to these documents.", new List<CitationSourceInfo>());
                    }

                    return (answer, filteredCitations);
                }

                return (string.Empty, new List<CitationSourceInfo>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SearchResultByOpenAIWithFullCitations. Container: {Container}, Query: {Query}, User: {UserEmail}",
                    containerName, chatInput, userEmail);
                throw;
            }
        }

        // Helper method to get accessible documents for a user
        private async Task<List<string>> GetAccessibleDocumentsForUser(string containerName, string userEmail, IAccessControlService accessControlService)
        {
            try
            {
                // Get all documents in the container
                var documents = new List<string>();

                // Create BlobServiceClient and ContainerClient to list all blobs
                var blobServiceClient = new BlobServiceClient(_configuration.GetSection("Storage")["connectionString"]);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

                if (await containerClient.ExistsAsync())
                {
                    await foreach (var blob in containerClient.GetBlobsAsync())
                    {
                        documents.Add(blob.Name);
                    }
                }

                if (documents.Count == 0)
                {
                    // No documents in container
                    return new List<string>();
                }

                // Get a list of accessible documents based on ACL
                var accessibleDocuments = new List<string>();

                foreach (var document in documents)
                {
                    var accessControl = await accessControlService.GetAccessControl(document, containerName);

                    // Document is accessible if:
                    // 1. It's open to the organization (IsOpen = true), or
                    // 2. The user is in the ACL
                    if (accessControl.IsOpen || accessControl.Acl.Contains(userEmail, StringComparer.OrdinalIgnoreCase))
                    {
                        accessibleDocuments.Add(document);
                    }
                }

                return accessibleDocuments;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting accessible documents for user {UserEmail} in container {Container}",
                    userEmail, containerName);
                return new List<string>();
            }
        }

        // Helper method to escape special characters in filter values
        private string EscapeFilterValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return value.Replace("'", "''");
        }

        // Helper method to find a property by trying multiple names
        private PropertyInfo FindProperty(object obj, params string[] propertyNames)
        {
            if (obj == null) return null;

            foreach (var name in propertyNames)
            {
                var prop = obj.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (prop != null)
                    return prop;
            }

            return null;
        }

        private void CreateChatCompletionOptions()
        {
            _options = new ChatCompletionsOptions()
            {
                Temperature = _chatSettings.Temperature,
                MaxTokens = _chatSettings.MaxTokens,
                FrequencyPenalty = _chatSettings.FrequencyPenalty,
                PresencePenalty = _chatSettings.PresencePenalty,
            };

            _options.AzureExtensionsOptions = new AzureChatExtensionsOptions();
            var searchExtension = new AzureCognitiveSearchChatExtensionConfiguration
            {
                SearchEndpoint = new Uri(_azuresearchServiceEndpoint),
                IndexName = "vector-general-index"
            };

            // Try to set the API key using reflection
            foreach (var propName in new[] { "SearchKey", "Key", "ApiKey", "KeyCredential" })
            {
                var prop = searchExtension.GetType().GetProperty(propName);
                if (prop != null)
                {
                    try
                    {
                        prop.SetValue(searchExtension, new AzureKeyCredential(_azuresearchApiKey));
                        break;
                    }
                    catch { }
                }
            }

            _options.AzureExtensionsOptions.Extensions.Add(searchExtension);
        }
    }
}