﻿using GraphQl.NetStandard.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace RepositoryAnalyticsApi.Repositories.Model.Github.GraphQL
{
    public class Team
    {
        public string Name { get; set; }

        [JsonConverter(typeof(GraphQlEdgesParentConverter<TeamToRepositoryEdge>))]
        public GraphQLEdgesParent<TeamToRepositoryEdge> RepositoryEdges { get; set; }
    }
}
