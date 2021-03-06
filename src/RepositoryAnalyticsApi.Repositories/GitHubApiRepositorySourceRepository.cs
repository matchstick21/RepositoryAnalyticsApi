﻿using GraphQl.NetStandard.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Octokit;
using Polly;
using Polly.Retry;
using RepositoryAnaltyicsApi.Interfaces;
using RepositoryAnalyticsApi.InternalModel;
using RepositoryAnalyticsApi.ServiceModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RepositoryAnalyticsApi.Repositories
{
    public class GitHubApiRepositorySourceRepository : IRepositorySourceRepository
    {
        const string DATE_TIME_ISO8601_FORMAT = "yyyy-MM-ddTHH:mm:ssZ";

        private IGitHubClient gitHubClient;
        private ITreesClient treesClient;
        private IGraphQLClient graphQLClient;
        private ILogger<GitHubApiRepositorySourceRepository> logger;
        private AsyncRetryPolicy githubAbuseRateLimitPolicy;

        public GitHubApiRepositorySourceRepository(IGitHubClient gitHubClient, ITreesClient treesClient, IGraphQLClient graphQLClient, ILogger<GitHubApiRepositorySourceRepository> logger)
        {
            this.gitHubClient = gitHubClient;
            this.treesClient = treesClient;
            this.graphQLClient = graphQLClient;
            this.logger = logger;

            this.githubAbuseRateLimitPolicy = Policy.Handle<GraphQLRequestException>(ex =>
                    ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden &&
                    ex.ResponseBody.Contains("You have triggered an abuse detection mechanism", StringComparison.OrdinalIgnoreCase))
              .WaitAndRetryAsync(5, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (exception, timespan) => 
                {
                    logger.LogWarning($"GitHub abuse rate limit hit, waiting {timespan.TotalSeconds} seconds before trying again");
                });
        }

        public async Task<string> ReadFileContentAsync(string owner, string name, string fullFilePath, string gitRef)
        {
            var repositoryContent = await gitHubClient.Repository.Content.GetAllContentsByRef(owner, name, fullFilePath, gitRef).ConfigureAwait(false);

            if (repositoryContent != null && repositoryContent.Any())
            {
                return repositoryContent.First().Content;
            }
            else
            {
                return null;
            }
        }

        public async Task<List<(string fullFilePath, string fileContent)>> GetMultipleFileContentsAsync(string repositoryOwner, string repositoryName, string gitRef, List<string> fullFilePaths)
        {
            var tupleList = new List<(string fullFilePath, string fileContent)>();


            var fileContentRequestBuilder = new StringBuilder();
            // Build up the list of file content requests.  This is needed because GraphQl does not allow
            // the returning of multiple nodes of the same name.  This loop builds up the file request json
            // aliases on these nodes
            for (int i = 0; i < fullFilePaths.Count; i++)
            {
                var fileContentRequestJson = GetFileContentRequestJson(i + 1, fullFilePaths[i]);
                fileContentRequestBuilder.Append(fileContentRequestJson);
            }

            var query = $@"
            query ($repoName:String!, $repoOwner:String!){{
              repository(name:$repoName,  owner: $repoOwner) {{
                    {fileContentRequestBuilder.ToString()}
                }}
            }}
            ";

            var variables = new { repoOwner = repositoryOwner, repoName = repositoryName };

            var responseBodyString = await graphQLClient.QueryAsync(query, variables).ConfigureAwait(false);

            var jObject = JObject.Parse(responseBodyString);

            // Parse the aliased file
            for (int i = 0; i < fullFilePaths.Count; i++)
            {
                var fileJObject = jObject["data"]["repository"][$"file{i + 1}"];

                if (fileJObject.HasValues)
                {
                    var fileContent = fileJObject["text"].Value<string>();

                    tupleList.Add((fullFilePaths[i], fileContent));
                }
                else
                {
                    throw new ArgumentException($"File {fullFilePaths[i]} not found when reading file contents for repo {repositoryName}. " +
                        $"This is likely the result of a stale file list cache for the repository");
                }
 
            }

            return tupleList;

            string GetFileContentRequestJson(int index, string fullFilePath)
            {
                return $@"
                file{index}: object(expression: ""{gitRef}:{fullFilePath}"") {{
                 ...on Blob {{
                     text
                    }}
                }}
            ";
            }
        }

        // The GraphQL Api does not support the recursive reading of files so using the V3 API
        public async Task<List<RepositoryFile>> ReadFilesAsync(string owner, string name, string gitRef)
        {
            var repoFiles = new List<RepositoryFile>();

            TreeResponse treeResponse = null;

            try
            {
                treeResponse = await treesClient.GetRecursive(owner, name, gitRef).ConfigureAwait(false);
            }
            catch (Octokit.ApiException ex) when (ex.Message == "Git Repository is empty.")
            {
                return repoFiles;   
            }
            
            var treeItems = treeResponse.Tree;
            
            if (treeItems != null && treeItems.Any())
            {
                var fileTreeItems = treeItems.Where(treeItem => treeItem.Type == TreeType.Blob);

                foreach (var fileTreeItem in fileTreeItems)
                {
                    var codeRepoFile = new RepositoryFile();
                    codeRepoFile.FullPath = fileTreeItem.Path;
                    codeRepoFile.Name = Path.GetFileName(codeRepoFile.FullPath);

                    repoFiles.Add(codeRepoFile);
                }
            }

            return repoFiles;
        }

        public async Task<RepositorySourceRepository> ReadRepositoryAsync(string repositoryOwner, string repositoryName)
        {
            var query = @"
            query ($repoName:String!, $repoOwner:String!){
              repository(owner: $repoOwner, name: $repoName){
	            name,
                url,
                pushedAt,
                createdAt,
                defaultBranchRef{
                  name
                }
                refs(first:100, refPrefix: ""refs/heads/"") {
                    nodes {
                        name
                    }
                }
                projects{
                  totalCount
                }
    		    issues{
                  totalCount
                }
                pullRequests{
                  totalCount
                }
                repositoryTopics(first:50){
                  nodes{
                    topic{
                      name
                    }
                  }
                }
              }
            }
            ";

            var variables = new { repoOwner = repositoryOwner, repoName = repositoryName };

            var repository = await graphQLClient.QueryAsync<Model.Github.GraphQL.Repository>(query, variables).ConfigureAwait(false);

            // TODO: Figure out if anything else needs to be done to accomodate repos without a default branch
            var repositorySourceRepository = new RepositorySourceRepository()
            {
                Name = repository.Name,
                Url = repository.Url,
                PushedAt = repository.PushedAt,
                CreatedAt = repository.CreatedAt,
                DefaultBranchName = repository.DefaultBranchRef?.Name,
                ProjectCount = repository.Projects.TotalCount.Value,
                IssueCount = repository.Issues.TotalCount.Value,
                PullRequestCount = repository.PullRequests.TotalCount.Value,
                TopicNames = new List<string>(),
                BranchNames = new List<string>()
            };
            if (repository.RepositoryTopics.Nodes.Any())
            {
                repositorySourceRepository.TopicNames.AddRange(repository.RepositoryTopics.Nodes.Select(topic => topic.Name));
            }

            if (repository.Refs.Nodes.Any())
            {
                repositorySourceRepository.BranchNames.AddRange(repository.Refs.Nodes.Select(@ref => @ref.Name));
            }

            return repositorySourceRepository;
        }

        public async Task<RepositorySummary> ReadRepositorySummaryAsync(string organization, string user, string name)
        {
            string loginType = null;
            string login = null;
            string endCursorQuerySegment = string.Empty;

            var query = @"
            query ($login: String!, $name: String!) {
              #LOGIN_TYPE#(login: $login) {
                repository(name: $name) {
                  url
                  createdAt
                  pushedAt
                }
              }
            }
            ";

            if (!string.IsNullOrWhiteSpace(user))
            {
                loginType = "user";
                login = user;
            }
            else
            {
                loginType = "organization";
                login = organization;
            }

            query = query.Replace("#LOGIN_TYPE#", loginType);

            var variables = new { login = login, name = name};

            Model.Github.GraphQL.Repository graphQLRepository = null;

            if (loginType == "user")
            {
                var graphQLUser = await graphQLClient.QueryAsync<Model.Github.GraphQL.User>(query, variables).ConfigureAwait(false);
                graphQLRepository = graphQLUser.Repository;
            }
            else
            {
                var graphQLOrganization = await graphQLClient.QueryAsync<Model.Github.GraphQL.Organization>(query, variables).ConfigureAwait(false);
                graphQLRepository = graphQLOrganization.Repository;
            }

            var repositorySummary = new RepositorySummary
            {
                CreatedAt = graphQLRepository.CreatedAt,
                UpdatedAt = graphQLRepository.PushedAt.Value,
                Url = graphQLRepository.Url,
            };

            return repositorySummary;
        }

        public async Task<CursorPagedResults<RepositorySummary>> ReadRepositorySummariesAsync(string organization, string user, int take, string endCursor)
        {
            string loginType = null;
            string login = null;

            var query = @"
            query ($login: String!, $take: Int, $after: String) {
              #LOGIN_TYPE#(login: $login) {
                repositories(first: $take, after: $after, orderBy: {field: PUSHED_AT, direction: DESC}) {
                  nodes {
                      url
                      createdAt
                      pushedAt
                  }
                  pageInfo {
                    hasNextPage
                    endCursor
                  }
                }
              }
            }
            ";


            if (!string.IsNullOrWhiteSpace(user))
            {
                loginType = "user";
                login = user;
            }
            else
            {
                loginType = "organization";
                login = organization;
            }

            query = query.Replace("#LOGIN_TYPE#", loginType);

            var variables = new { login = login, take = take, after = endCursor};

            GraphQlNodesParent<Model.Github.GraphQL.Repository> graphQLRepositories = null;

            if (loginType == "user")
            {
                var graphQLUser = await graphQLClient.QueryAsync<Model.Github.GraphQL.User>(query, variables).ConfigureAwait(false);
                graphQLRepositories = graphQLUser.Repositories;
            }
            else
            {
                var graphQLOrganization = await graphQLClient.QueryAsync<Model.Github.GraphQL.Organization>(query, variables).ConfigureAwait(false);
                graphQLRepositories = graphQLOrganization.Repositories;
            }

            var cursorPagedResults = new CursorPagedResults<RepositorySummary>();
            cursorPagedResults.EndCursor = graphQLRepositories.PageInfo.EndCursor;
            cursorPagedResults.MoreToRead = graphQLRepositories.PageInfo.HasNextPage;

            var results = new List<RepositorySummary>();

            foreach (var graphQLRepository in graphQLRepositories.Nodes)
            {
                var repositorySummary = new RepositorySummary
                {
                    CreatedAt = graphQLRepository.CreatedAt,
                    UpdatedAt = graphQLRepository.PushedAt.Value,
                    Url = graphQLRepository.Url
                };

                results.Add(repositorySummary);
            }

            cursorPagedResults.Results = results;

            return cursorPagedResults;
        }

        public async Task<RepositorySourceSnapshot> ReadRepositorySourceSnapshotAsync(string organization, string user, string name, string branch, DateTime? asOf)
        {
            string loginType = null;
            string login = null;
            string endCursorQuerySegment = string.Empty;

            var query = @"
            query ($login: String!, $name: String!, $branch: String!, $asOf: GitTimestamp) {
              #LOGIN_TYPE#(login: $login) {
                repository(name: $name) {
                  commitHistory: object(expression: $branch) {
                    ... on Commit {
                      history(first: 1, until: $asOf) {
                        nodes {
                          tree {
                            oid 
                          }
                          message
                          pushedDate
                          committedDate
                          id
                        }
                      }
                    }
                  }
                }
              }
            }
            ";

            if (!string.IsNullOrWhiteSpace(user))
            {
                loginType = "user";
                login = user;
            }
            else
            {
                loginType = "organization";
                login = organization;
            }

            query = query.Replace("#LOGIN_TYPE#", loginType);

            string asOfGitTimestamp = null;

            if (asOf.HasValue)
            {
                asOfGitTimestamp = asOf.Value.ToString(DATE_TIME_ISO8601_FORMAT);
            }

            var variables = new { login = login, name = name, branch = branch, asOf = asOfGitTimestamp };

            Model.Github.GraphQL.Repository graphQLRepository = null;

            if (loginType == "user")
            {
                var graphQLUser = await graphQLClient.QueryAsync<Model.Github.GraphQL.User>(query, variables).ConfigureAwait(false);
                graphQLRepository = graphQLUser.Repository;
            }
            else
            {
                var graphQLOrganization = await graphQLClient.QueryAsync<Model.Github.GraphQL.Organization>(query, variables).ConfigureAwait(false);
                graphQLRepository = graphQLOrganization.Repository;
            }

            var repositorySummary = new RepositorySourceSnapshot
            {
                ClosestCommitId = graphQLRepository.CommitHistory.History.Nodes.First().Id,
                ClosestCommitPushedDate = graphQLRepository.CommitHistory.History.Nodes.First().PushedDate,
                ClosestCommitCommittedDate = graphQLRepository.CommitHistory.History.Nodes.First().CommittedDate,
                ClosestCommitTreeId = graphQLRepository.CommitHistory.History.Nodes.First().Tree.Oid
            };

            return repositorySummary;
        }

        public async Task<Dictionary<string, List<TeamRepositoryConnection>>> ReadTeamToRepositoriesMaps(string organization)
        {
            var teamToRespositoriesMap = new Dictionary<string, List<TeamRepositoryConnection>>();

            var moreTeamsToRead = true;
            string teamAfterCursor = null;

            logger.LogDebug($"Reading team information for organziation {organization}");

            while (moreTeamsToRead)
            {
                var allTeamsRepositoriesQuery = @"
                query ($login: String!, $afterCursor: String) {
                  organization(login: $login) {
                    teams(first:100, after: $afterCursor, orderBy:{field:NAME, direction:ASC}){
                      nodes{
                        name,
                        repositoryEdges: repositories(first:100){
                          edges{
                            permission
                            repository : node {
                                name
                            }
                          }
                          pageInfo{
                            endCursor,
                            hasNextPage
                          }
                        }
                      },
                      pageInfo{
                        endCursor,
                        hasNextPage
                      }
                    }
                  }
                }
               ";

                var allTeamsRepositoriesVariables = new { login = organization, afterCursor = teamAfterCursor };

                var graphQLOrganization = await githubAbuseRateLimitPolicy.ExecuteAsync(() =>
                {
                   return graphQLClient.QueryAsync<Model.Github.GraphQL.Organization>(allTeamsRepositoriesQuery, allTeamsRepositoriesVariables);
                }).ConfigureAwait(false);

                if (graphQLOrganization.Teams.Nodes != null && graphQLOrganization.Teams.Nodes.Any())
                {
                    foreach (var team in graphQLOrganization.Teams.Nodes)
                    {
                        var teamRepositoryConnections = new List<TeamRepositoryConnection>();

                        foreach (var teamRepositoryEdge in team.RepositoryEdges.Edges)
                        {
                            var teamRepositoryConnection = new TeamRepositoryConnection
                            {
                                RepositoryName = teamRepositoryEdge.Repository.Name,
                                TeamPermissions = teamRepositoryEdge.Permission
                            };

                            teamRepositoryConnections.Add(teamRepositoryConnection);
                        }

                        // Now get the additional pages of repositories if we need to
                        bool moreTeamRepositoriesToRead = team.RepositoryEdges.PageInfo.HasNextPage;
                        var afterCursor = team.RepositoryEdges.PageInfo.EndCursor;

                        while (moreTeamRepositoriesToRead)
                        {
                            logger.LogDebug($"Reading additional repository information for team {team.Name}");

                            var result = await GetAdditionalTeamRepositoriesAsync(team.Name, afterCursor).ConfigureAwait(false);

                            teamRepositoryConnections.AddRange(result.TeamRepositoryConnections);

                            if (result.AfterCursor != null)
                            {
                                moreTeamRepositoriesToRead = true;
                                afterCursor = result.AfterCursor;
                            }
                            else
                            {
                                moreTeamRepositoriesToRead = false;
                            }
                        }

                        teamToRespositoriesMap.Add(team.Name, teamRepositoryConnections);
                    }
                }

                moreTeamsToRead = graphQLOrganization.Teams.PageInfo.HasNextPage;

                if (moreTeamsToRead)
                {
                    teamAfterCursor = graphQLOrganization.Teams.PageInfo.EndCursor;
                    logger.LogDebug($"Reading additional team information for organziation {organization}");
                }
            }

            logger.LogDebug($"Finished reading team information for organziation {organization}");

            return teamToRespositoriesMap;

            async Task<(List<TeamRepositoryConnection> TeamRepositoryConnections, string AfterCursor)> GetAdditionalTeamRepositoriesAsync(string teamName, string afterCursor)
            {
                var teamRepositoriesQuery = @"
                    query ($login: String!, $teamName: String!, $repositoriesAfter: String) {
                      organization(login: $login) {
                        teams(first: 1, query: $teamName) {
                          nodes {
                            name
                            repositoryEdges: repositories(first:100, after: $repositoriesAfter){
                              edges{
                                permission
                                repository : node {
                                    name
                                }
                              }
                              pageInfo{
                                endCursor,
                                hasNextPage
                              }
                            }
                          }
                        }
                      }
                    }
                ";

                var teamRepositoriesVariables = new { login = organization, teamName = teamName, repositoriesAfter = afterCursor };

                var graphQLOrganization = await githubAbuseRateLimitPolicy.ExecuteAsync(() =>
                {
                    return graphQLClient.QueryAsync<Model.Github.GraphQL.Organization>(teamRepositoriesQuery, teamRepositoriesVariables);
                }).ConfigureAwait(false);

                var teamRepositoryConnections = new List<TeamRepositoryConnection>();

                foreach (var repositoryEdge in graphQLOrganization.Teams.Nodes.First().RepositoryEdges.Edges)
                {
                    var teamRepositoryConnection = new TeamRepositoryConnection
                    {
                        RepositoryName = repositoryEdge.Repository.Name,
                        TeamPermissions = repositoryEdge.Permission
                    };

                    teamRepositoryConnections.Add(teamRepositoryConnection);
                }

                string nextAfterCursor = null;

                if (graphQLOrganization.Teams.Nodes.First().RepositoryEdges.PageInfo.HasNextPage)
                {
                    nextAfterCursor = graphQLOrganization.Teams.Nodes.First().RepositoryEdges.PageInfo.EndCursor;
                }

                return (teamRepositoryConnections, nextAfterCursor);
            }
        }

        public async Task<OwnerType> ReadOwnerType(string owner)
        {
            var query = @"
            query ($login: String!) {
              repositoryOwner(login: $login){
                __typename
              }
            }
            ";

            var variables = new { login = owner };

            var repositoryOwner = await graphQLClient.QueryAsync<Model.Github.GraphQL.RepositoryOwner>(query, variables).ConfigureAwait(false);

            var ownerType = (OwnerType)Enum.Parse(typeof(OwnerType), repositoryOwner.TypeName);

            return ownerType;
        }
    }
}
