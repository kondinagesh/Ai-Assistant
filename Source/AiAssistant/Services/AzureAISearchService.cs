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
using System.Web;
using Azure.Search.Documents.Models;

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

        // Helper method to clean and standardize file names
        private string CleanFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return fileName;

            // Remove any path information and keep just the filename
            string cleanName = System.IO.Path.GetFileName(fileName);

            // Remove any URL encoding or special characters
            cleanName = HttpUtility.UrlDecode(cleanName);

            // Trim any whitespace
            cleanName = cleanName.Trim();

            return cleanName;
        }

        // Helper method to truncate content to a maximum length
        private string TruncateContent(string content, int maxLength)
        {
            if (string.IsNullOrEmpty(content) || content.Length <= maxLength)
                return content;

            // Try to truncate at a sentence boundary if possible
            int lastPeriod = content.LastIndexOf('.', maxLength - 1);
            if (lastPeriod > maxLength / 2)
                return content.Substring(0, lastPeriod + 1) + " [content truncated for length]";

            // Fall back to hard truncation
            return content.Substring(0, maxLength) + " [content truncated for length]";
        }

        // Helper method to escape special characters in filter values
        private string EscapeFilterValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return value.Replace("'", "''");
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

                _logger.LogInformation("Starting search for user {UserEmail} in container {Container} with query: {Query}",
                    userEmail, containerName, chatInput);

                // Step 1: Get list of documents the user has access to
                var accessControlService = _serviceProvider.GetRequiredService<IAccessControlService>();
                var accessibleDocuments = await GetAccessibleDocumentsForUser(containerName, userEmail, accessControlService);

                _logger.LogInformation("User {UserEmail} has access to {Count} documents in container {Container}: {Documents}",
                    userEmail, accessibleDocuments.Count, containerName, string.Join(", ", accessibleDocuments));

                if (accessibleDocuments.Count == 0)
                {
                    return ("I'm sorry, but you don't have access to any documents in this container.", new List<CitationSourceInfo>());
                }

                // Step 2: Perform a direct search to get relevant documents
                var indexName = $"vector-{containerName}-index";
                _logger.LogInformation("Using search index: {IndexName}", indexName);

                try
                {
                    // Create search client
                    var searchClient = new SearchClient(
                        new Uri(_azuresearchServiceEndpoint),
                        indexName,
                        new AzureKeyCredential(_azuresearchApiKey),
                        new SearchClientOptions
                        {
                            Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(new HttpClientHandler
                            {
                                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                            }))
                        });

                    // Perform a test search first to make sure the index is working
                    var testOptions = new SearchOptions
                    {
                        Size = 5,
                        IncludeTotalCount = true,
                        Select = { "title" }
                    };

                    var testSearch = await searchClient.SearchAsync<SearchDocument>("*", testOptions);
                    _logger.LogInformation("Test search found {Count} total documents in the index", testSearch.Value.TotalCount);

                    if (testSearch.Value.TotalCount == 0)
                    {
                        _logger.LogWarning("No documents found in search index. Check if indexer has run.");
                        return ("I couldn't find any documents in the search index. Please ensure documents have been uploaded and indexed.", new List<CitationSourceInfo>());
                    }

                    // Search for documents related to the query
                    var searchOptions = new SearchOptions
                    {
                        Size = 20,  // Get a good number of potential matches
                        IncludeTotalCount = true,
                        Select = { "title", "chunk" }
                    };

                    _logger.LogInformation("Performing search with query: {Query}", chatInput);

                    // Search for relevant documents
                    var searchResults = await searchClient.SearchAsync<SearchDocument>(chatInput, searchOptions);
                    _logger.LogInformation("Search found {Count} results for query", searchResults.Value.TotalCount);

                    // Step 3: Extract document content and filter by accessibility
                    var userAccessibleDocuments = new List<(string filename, string content, int docIndex)>();
                    var documentContents = new Dictionary<string, string>();

                    // We'll use this to create doc indices that are consistent across sessions
                    var docIndexMapping = new Dictionary<string, int>();

                    int nextIndex = 1;

                    // First pass: Get all document titles and assign indices
                    foreach (var result in searchResults.Value.GetResults())
                    {
                        SearchDocument document = result.Document;

                        // Try to get the title
                        if (document.TryGetValue("title", out object titleObj) && titleObj != null)
                        {
                            string rawTitle = titleObj.ToString();
                            string fileName = CleanFileName(rawTitle);

                            _logger.LogInformation("Found search result with title: {Title}", fileName);

                            // Check if we've already assigned an index to this document
                            if (!docIndexMapping.ContainsKey(fileName))
                            {
                                docIndexMapping[fileName] = nextIndex++;
                                _logger.LogInformation("Assigned index {Index} to document {FileName}", docIndexMapping[fileName], fileName);
                            }

                            // Extract content
                            if (document.TryGetValue("chunk", out object contentObj) && contentObj != null)
                            {
                                string content = contentObj.ToString();

                                // If we already have content for this document, append the new content
                                if (documentContents.TryGetValue(fileName, out string existingContent))
                                {
                                    documentContents[fileName] = existingContent + "\n" + content;
                                }
                                else
                                {
                                    documentContents[fileName] = content;
                                }

                                _logger.LogInformation("Added content for document {FileName}, length now: {Length}",
                                    fileName, documentContents[fileName].Length);
                            }
                            else
                            {
                                _logger.LogWarning("Search result for {FileName} is missing content/chunk property", fileName);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Search result missing title property");
                        }
                    }

                    // Fallback: If no results were found or title/content extraction failed, use all accessible documents
                    if (documentContents.Count == 0)
                    {
                        _logger.LogWarning("No valid search results found. Using all accessible documents as fallback.");

                        // Create a placeholder document for each accessible document
                        foreach (var doc in accessibleDocuments)
                        {
                            string fileName = CleanFileName(doc);
                            docIndexMapping[fileName] = nextIndex++;
                            documentContents[fileName] = $"This is document {fileName}. No content was extracted from the search index.";
                        }
                    }

                    // Second pass: Check which documents the user has access to
                    foreach (var document in documentContents)
                    {
                        string fileName = document.Key;
                        string content = document.Value;

                        // Check if the user has access to this document
                        bool hasAccess = accessibleDocuments.Any(accessDoc =>
                            accessDoc.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                            System.IO.Path.GetFileName(accessDoc).Equals(fileName, StringComparison.OrdinalIgnoreCase));

                        if (hasAccess && docIndexMapping.TryGetValue(fileName, out int docIndex))
                        {
                            userAccessibleDocuments.Add((fileName, content, docIndex));
                            _logger.LogInformation("User has access to relevant document: {FileName} (index: {Index})", fileName, docIndex);
                        }
                        else
                        {
                            _logger.LogInformation("Document {FileName} is relevant but user does not have access", fileName);
                        }
                    }

                    // Check if we have any accessible relevant documents
                    if (userAccessibleDocuments.Count == 0)
                    {
                        _logger.LogWarning("No accessible relevant documents found for the query");
                        return ("I couldn't find any relevant documents that you have access to for this query.", new List<CitationSourceInfo>());
                    }

                    // Step 4: Build a custom prompt with ONLY the filtered documents
                    string customPrompt = $"Question: {chatInput}\n\nHere are the ONLY documents you can use to answer the question:\n\n";

                    foreach (var doc in userAccessibleDocuments)
                    {
                        // Limit content length to avoid exceeding context window
                        string truncatedContent = TruncateContent(doc.content, 1000);
                        customPrompt += $"[doc{doc.docIndex}] {doc.filename}:\n{truncatedContent}\n\n";
                    }

                    customPrompt += "IMPORTANT INSTRUCTIONS:\n";
                    customPrompt += "1. Answer ONLY based on the documents provided above.\n";
                    customPrompt += "2. Do NOT mention or refer to any information not in these documents.\n";
                    customPrompt += "3. For any information you use, include the citation (e.g. [doc1], [doc2]) after each relevant piece of information.\n";
                    customPrompt += "4. If multiple documents contain relevant information to the query, you MUST reference ALL of them.\n";
                    customPrompt += "5. If the documents don't contain the answer, say you don't have enough information.\n";

                    _logger.LogInformation("Sending custom prompt with {Count} accessible documents", userAccessibleDocuments.Count);

                    // Step 5: Send to OpenAI without search extension
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

                    // Add system message with instructions
                    options.Messages.Add(new ChatMessage(ChatRole.System,
                        @"You are a helpful assistant that ONLY provides information based on the documents explicitly provided to you.
                        
                        CRITICAL INSTRUCTIONS:
                        - NEVER share information from documents you haven't been given
                        - DO NOT make up or infer information not explicitly provided in the documents
                        - If the documents don't contain the answer, say you don't have enough information
                        - Always include citation markers [doc1], [doc2], etc. after using information from a document
                        - IMPORTANT: If multiple documents contain relevant information, you MUST reference ALL of them
                        - Always cite EVERY relevant document, not just a subset
                        
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

                    // Add user message with the custom prompt containing filtered documents
                    options.Messages.Add(new ChatMessage(ChatRole.User, customPrompt));

                    string chatDeploymentId = _configuration.GetValue<string>("AzOpenAIChatDeploymentId") ?? "gpt-4o";
                    options.DeploymentName = chatDeploymentId;

                    // Call OpenAI
                    var response = await client.GetChatCompletionsAsync(options);

                    if (response?.Value?.Choices != null && response.Value.Choices.Count > 0)
                    {
                        string answer = response.Value.Choices[0].Message.Content;
                        _logger.LogInformation("Received response from OpenAI: {Length} characters", answer?.Length ?? 0);

                        // Extract citation markers ([doc1], [doc2], etc.) from the answer
                        var citationMatches = System.Text.RegularExpressions.Regex.Matches(answer, @"\[doc(\d+)\]");
                        var citationNumbers = new HashSet<int>();

                        foreach (System.Text.RegularExpressions.Match match in citationMatches)
                        {
                            if (int.TryParse(match.Groups[1].Value, out int citationNumber))
                            {
                                citationNumbers.Add(citationNumber);
                            }
                        }

                        // Filter the citations to only include those referenced in the text and accessible to user
                        var referencedCitations = new List<CitationSourceInfo>();

                        foreach (var docNumber in citationNumbers)
                        {
                            var matchingDoc = userAccessibleDocuments.FirstOrDefault(d => d.docIndex == docNumber);
                            if (matchingDoc != default)
                            {
                                // Create shortened content for citation displays
                                string shortContent = TruncateContent(matchingDoc.content, 500);

                                referencedCitations.Add(new CitationSourceInfo
                                {
                                    Source = matchingDoc.filename,
                                    Content = shortContent,
                                    Index = matchingDoc.docIndex
                                });

                                _logger.LogInformation("Added citation {DocNumber}: {Filename}", docNumber, matchingDoc.filename);
                            }
                            else
                            {
                                _logger.LogWarning("Reference to unknown document index in response: [doc{DocNumber}]", docNumber);
                            }
                        }

                        _logger.LogInformation("Response references {Count} documents: {Citations}",
                            referencedCitations.Count,
                            string.Join(", ", referencedCitations.Select(c => $"[doc{c.Index}] {c.Source}")));

                        return (answer, referencedCitations);
                    }

                    return ("I'm sorry, but I couldn't generate a response based on the available documents.", new List<CitationSourceInfo>());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error searching or processing search results");
                    return ("I encountered an error while searching the documents. Please try again later.", new List<CitationSourceInfo>());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SearchResultByOpenAIWithFullCitations. Container: {Container}, Query: {Query}, User: {UserEmail}",
                    containerName, chatInput, userEmail);
                return ("I encountered an error while searching the documents. Please try again later.", new List<CitationSourceInfo>());
            }
        }

        private async Task<List<string>> GetAccessibleDocumentsForUser(string containerName, string userEmail, IAccessControlService accessControlService)
        {
            try
            {
                if (string.IsNullOrEmpty(userEmail))
                {
                    _logger.LogWarning("User email is empty, cannot determine document access");
                    return new List<string>();
                }

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
                    _logger.LogWarning("No documents found in container {Container}", containerName);
                    return new List<string>();
                }

                _logger.LogInformation("Found {Count} documents in container {Container}", documents.Count, containerName);

                // Get a list of accessible documents based on ACL
                var accessibleDocuments = new List<string>();

                foreach (var document in documents)
                {
                    try
                    {
                        var accessControl = await accessControlService.GetAccessControl(document, containerName);

                        // Document is accessible if:
                        // 1. It's open to the organization (IsOpen = true), or
                        // 2. The user is in the ACL
                        bool isOpen = accessControl.IsOpen;

                        // Get all user emails from ACL for logging
                        string aclList = string.Join(", ", accessControl.Acl);

                        bool userInAcl = false;
                        if (accessControl.Acl != null && accessControl.Acl.Count > 0)
                        {
                            userInAcl = accessControl.Acl.Contains(userEmail, StringComparer.OrdinalIgnoreCase);
                        }

                        if (isOpen || userInAcl)
                        {
                            accessibleDocuments.Add(document);
                            _logger.LogInformation("Document {Document} is accessible to user {UserEmail}. IsOpen={IsOpen}, UserInAcl={UserInAcl}, ACL={ACL}",
                                document, userEmail, isOpen, userInAcl, aclList);
                        }
                        else
                        {
                            _logger.LogInformation("Document {Document} is NOT accessible to user {UserEmail}. IsOpen={IsOpen}, UserInAcl={UserInAcl}, ACL={ACL}",
                                document, userEmail, isOpen, userInAcl, aclList);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error checking access control for document {Document}", document);
                        // Skip this document if there's an error checking access
                        continue;
                    }
                }

                _logger.LogInformation("User {UserEmail} has access to {Count}/{Total} documents in container {Container}: {Documents}",
                    userEmail, accessibleDocuments.Count, documents.Count, containerName, string.Join(", ", accessibleDocuments));

                return accessibleDocuments;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting accessible documents for user {UserEmail} in container {Container}",
                    userEmail, containerName);
                return new List<string>();
            }
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