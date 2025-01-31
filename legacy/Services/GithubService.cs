using System.Net.Http.Headers;
using System.Text.Json;
using Ghost.Legacy.Infrastructure;
using System.Text.Json.Serialization;

namespace Ghost.Legacy.Services;

public class GithubService
{
    private readonly HttpClient _httpClient;

    public GithubService()
    {
        _httpClient = new HttpClient
        {
                BaseAddress = new Uri("https://api.github.com")
        };
    }

    public async Task<string> CreateRepositoryAsync(string name, string token)
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ghost", "1.0"));

        var requestBody = JsonSerializer.Serialize(new
        {
                name = name,
                description = $"Created with Ghost CLI",
                //private = false,
                auto_init = false
        });

        try
        {
            var content = new StringContent(requestBody);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await _httpClient.PostAsync("/user/repos", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Try to parse GitHub error message
                try
                {
                    var error = JsonSerializer.Deserialize<GitHubErrorResponse>(responseContent);
                    if (error?.Message != null)
                    {
                        throw new GhostException(
                                $"GitHub API error: {error.Message}" +
                                (error.Errors?.Any() == true ? $"\nDetails: {string.Join(", ", error.Errors.Select(e => e.Message))}" : ""),
                                ErrorCode.GithubError);
                    }
                }
                catch
                {
                    throw new GhostException(
                            $"Failed to create repository. Status code: {response.StatusCode}",
                            ErrorCode.GithubError);
                }
            }

            var repoInfo = JsonSerializer.Deserialize<GitHubRepository>(responseContent);
            if (repoInfo?.CloneUrl == null)
            {
                throw new GhostException("Repository created but URL not found in response", ErrorCode.GithubError);
            }

            return repoInfo.CloneUrl;
        }
        catch (Exception ex) when (ex is not GhostException)
        {
            throw new GhostException($"Failed to create repository: {ex.Message}", ErrorCode.GithubError);
        }
    }

    private class GitHubRepository
    {
        [JsonPropertyName("clone_url")]
        public string CloneUrl { get; set; }
    }

    private class GitHubErrorResponse
    {
        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("errors")]
        public List<GitHubError> Errors { get; set; }
    }

    private class GitHubError
    {
        [JsonPropertyName("resource")]
        public string Resource { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; }

        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }
    }
}
