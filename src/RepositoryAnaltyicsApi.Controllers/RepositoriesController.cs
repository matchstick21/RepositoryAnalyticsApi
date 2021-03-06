﻿using Microsoft.AspNetCore.Mvc;
using RepositoryAnaltyicsApi.Interfaces;
using RepositoryAnalyticsApi.ServiceModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RepositoryAnaltyicsApi.Controllers
{
    [Produces("application/json")]
    [Route("api/repoistories")]
    [ApiController]
    public class RepositoriesController : ControllerBase
    {
        private IRepositoryManager repositoryManager;
        // Get any non digit portion at the start of the version number.  If there is one assume it's a range
        // specifier like >=
        private string rangeSpecifierRegex = @"^[^\w]+";

        public RepositoriesController(IRepositoryManager repositoryManager)
        {
            this.repositoryManager = repositoryManager;
        }

        /// <summary>
        /// Search repositories
        /// </summary>
        /// <param name="typeName">Exact match</param>
        /// <param name="implementationName">Exact match</param>
        /// <param name="dependencies">Exact match on name</param>
        /// <remarks>
        /// Dependency filters can be one of the following four flavors and you can have multiple filters specified
        /// 1. Just a name
        ///
        ///     `.NET Framework`
        /// 2. A name and an exact version you want to match, with a `:` seperating the two
        ///
        ///     `.NET Framework:4.6.2`
        /// 3. A name and a partial version you want to match
        ///
        ///     `.NET Framwork:4.6` will match versions `4.6.1`, `4.6.2`, etc.
        /// 4. A name and a version with a range specifier (>, >=, etc)
        ///
        ///    `.NET Framework:>=4` will match versions `4.x.x`, `5.x.x`, etc.
        /// </remarks>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<List<string>>> GetAsync(
            string typeName,
            string implementationName,
            // Due to a bug in Asp.Net Core 2.1 need to have the [FromQuery] attribute along with the name specified 
            // https://github.com/aspnet/Mvc/issues/8126  Should be fixed in 2.2
            [FromQuery(Name = "dependencies")]string[] dependencies,
            bool? hasContinuousDelivery,
            DateTime? asOf,
            string topic,
            string team
        )
        {
            var parsedDependencies = new List<(string Name, string Version, RangeSpecifier RangeSpecifier)>();

            if (dependencies.Any())
            {
                foreach (var dependency in dependencies)
                {
                    var dependencyParts = dependency.Split(':');

                    var dependencyName = dependencyParts[0];
                    string dependencyVersion = null;

                    if (dependencyParts.Length == 2)
                    {
                        dependencyVersion = dependencyParts[1];
                    }

                    if (string.IsNullOrWhiteSpace(dependencyVersion))
                    {
                        parsedDependencies.Add((dependencyParts[0], null, RangeSpecifier.Unspecified));
                    }
                    else
                    {
                        var rangeSpecifier = RangeSpecifier.Unspecified;

                        var match = Regex.Match(dependencyVersion, rangeSpecifierRegex);

                        if (match.Success)
                        {
                            switch (match.Value)
                            {
                                case ">=":
                                    dependencyVersion = dependencyVersion.Replace(">=", "");
                                    rangeSpecifier = RangeSpecifier.GreaterThanOrEqualTo;
                                    break;
                                case ">":
                                    dependencyVersion = dependencyVersion.Replace(">", "");
                                    rangeSpecifier = RangeSpecifier.GreaterThan;
                                    break;
                                case "<":
                                    dependencyVersion = dependencyVersion.Replace("<", "");
                                    rangeSpecifier = RangeSpecifier.LessThan;
                                    break;
                                case "<=":
                                    dependencyVersion = dependencyVersion.Replace("<=", "");
                                    rangeSpecifier = RangeSpecifier.LessThanOrEqualTo;
                                    break;
                            }
                        }

                        parsedDependencies.Add((dependencyName, dependencyVersion, rangeSpecifier));
                    }
                }
            }

            var repositorySearch = new RepositorySearch
            {
                TypeName = typeName,
                ImplementationName = implementationName,
                HasContinuousDelivery = hasContinuousDelivery,
                Dependencies = parsedDependencies,
                AsOf = asOf,
                Team = team,
                Topic = topic
            };

            var repositoryNames = await repositoryManager.SearchAsync(repositorySearch);

            return repositoryNames;
        }
    }
}
