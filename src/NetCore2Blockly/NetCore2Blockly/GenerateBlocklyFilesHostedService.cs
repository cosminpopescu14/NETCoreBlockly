﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Readers;
using NetCore2Blockly.Swagger;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetCore2Blockly
{
    /// <summary>
    /// host to enumerate
    /// </summary>
    /// <seealso cref="Microsoft.Extensions.Hosting.IHostedService" />
    public class GenerateBlocklyFilesHostedService : IHostedService
    {

        #region swaggers
        private Dictionary<string, BlocklyFileGenerator> swaggers;

        internal async Task AddSwagger(string key, string endpoint)
        {
            try
            {
                var data = await GenerateFromSwaggerEndPoint(endpoint);

                swaggers.Add(key, new BlocklyFileGenerator(data));
            }
            catch(Exception ex)
            {
                Console.WriteLine($"adding swagger {endpoint} throws error {ex?.Message}");
                //swallowing error - should run even if the endpoint is not available
                
            }
        }
        internal string[] KeySwaggers()
        {
            return swaggers.Select(it => it.Key).ToArray();
        }
        internal string SwaggerBlocklyToolBoxFunctionDefinition()
        {
            return string.Join(Environment.NewLine, swaggers.Select(it => it.Value.GenerateBlocklyToolBoxFunctionDefinitionFile(it.Key))); ;
        }
        internal string SwaggerBlocklyAPIFunctions()
        {
            return string.Join(Environment.NewLine, swaggers.Select(it => it.Value.GenerateBlocklyAPIFunctions(it.Key)));
        }
        internal string SwaggerBlocklyToolBoxValueDefinition()
        {
            return string.Join(Environment.NewLine, swaggers.Select(it => it.Value.GenerateBlocklyToolBoxValueDefinitionFile(it.Key)));
        }
        internal string SwaggerBlocklyTypesDefinition()
        {

            return string.Join(Environment.NewLine, swaggers.Select(it => it.Value.GenerateNewBlocklyTypesDefinition()));
            
        }
        internal string SwaggersDictionaryJS
        {
            get
            {
                var s = string.Join(Environment.NewLine,
                    swaggers.Select(it => $@"dictSwagger.push({{key:'{it.Key}',value:'{it.Value}'}});"));

                return $@"var dictSwagger=[]; {s}";
            }
        }
        async Task<List<ActionInfo>> GenerateFromSwaggerEndPoint(string endpoint)
        {
            var uri = new Uri(endpoint);
            var site = uri.Scheme + "://" + uri.Authority;
            
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(site)
            };
            using var stream = await httpClient.GetStreamAsync(uri.PathAndQuery);
            var openApiDocument = new OpenApiStreamReader().Read(stream, out var diagnostic);
            var servers = openApiDocument.Servers;
            if (servers?.Count > 0)
            {
                foreach (var server in servers)
                {
                    if (server.Url.StartsWith(site))
                    {
                        site = server.Url;
                        break;
                    }
                }
            }
            var comp = openApiDocument.Components;

            var types = new AllTypes();
            foreach (var schema in comp.Schemas)
            {

                var t = new TypeToGenerateSwagger(schema);
                t.Site = site;
                types.Add(t);
            }
            //do it again to find property types
            var existingTypes = new AllTypes(types);
            

           foreach(var t in existingTypes)
            {
                foreach(var prop in t.GetProperties())
                {
                    if (prop.PropertyType != null)
                        continue;
                    if (!(prop is PropertyBaseSwagger pbs))
                        continue;
                    var val = pbs.propertyTypeSchema;
                    var reference = val.Reference;
                    if (reference != null)
                    {
                        prop.PropertyType = types.FindAfterId(reference.ReferenceV2 + "_" + reference.ReferenceV3);
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(val.Type))
                    {
                        prop.PropertyType = types.FindAfterId(val.Type);
                        continue;
                    }
                    //should almost never happen , but , if not discovered...
                    prop.PropertyType = types.FindAfterId(null);
                    //Debug.Assert(prop.PropertyType != null);
                    

                }
            }
            var functions = openApiDocument.Paths;
            var actions = new List<ActionInfo>();
            foreach (var f in functions)
            {
                var val = f.Value;
                foreach (var op in val.Operations)
                {
                    var val1 = op.Value;
                    var act = new ActionInfoSwagger();
                    actions.Add(act);
                    act.Site = site;
                    act.Verb = op.Key.GetDisplayName();
                    act.RelativeRequestUrl = f.Key;
                    act.ActionName =TypeToGenerateSwagger.Prefix(site) + "_"+ act.RelativeRequestUrl;
                    var R200 = val1.Responses.FirstOrDefault(it => it.Key == "200");
                    var type = R200.Value?.Content?.FirstOrDefault().Value?.Schema?.Type;
                    if (!string.IsNullOrEmpty(type) && type !="object")
                        act.ReturnType = types.FindAfterId(type);

                    if(act.ReturnType == null)
                    {
                        var refer= R200.Value?.Content?.FirstOrDefault().Value?.Schema?.Reference;
                        if(refer != null)
                        act.ReturnType = types.FindAfterId(refer.ReferenceV2 + "_" + refer.ReferenceV3);

                    }
                    if(act.ReturnType == null)
                    {
                        act.ReturnType = types.FindAfterId(null);//null type
                    }
                    
                    if (val1.Tags?.Count > 0)
                    {
                        act.ControllerName = val1.Tags.First().Name;
                    }
                    else
                    {
                        act.ControllerName = ActionInfoSwagger.GenerateControllerName(act.RelativeRequestUrl);
                    }
                    foreach (var par in val1.Parameters)
                    {
                        var name = par.Name;
                        var s = par.In;//path , query
                        var schema = par.Schema;
                        TypeArgumentBase myType = null;
                        if (schema.Type != null)
                        {
                            myType = types.FindAfterId(schema.Type);
                        }
                        if (schema.Reference != null)
                        {
                            var id = schema.Reference.ReferenceV2 + "_" + schema.Reference.ReferenceV3;
                            myType = types.FindAfterId(id);

                        }
                        var bs = BindingSourceDefinition.None;
                        switch (par.In.GetDisplayName().ToLower())
                        {
                            case "path":
                                bs = BindingSourceDefinition.Path;
                                break;
                            case "query":
                                bs = BindingSourceDefinition.Query;
                                break;
                            default:
                                //https://swagger.io/docs/specification/describing-parameters/
                                break;

                        }
                        act.Params.Add(name, (myType, bs));
                    }
                    var postData = val1.RequestBody?.Content;
                    if (postData != null )
                    {
                        var bs = BindingSourceDefinition.Body;
                        var data = postData.Values.FirstOrDefault();
                        var s = data.Schema;
                        var name = s.Type;
                        if (name != null)
                        {
                            TypeArgumentBase myType = null;
                            if (s.Reference != null)
                            {
                                var id = s.Reference.ReferenceV2 + "_" + s.Reference.ReferenceV3;
                                myType = types.FindAfterId(id);
                                name = myType.Name;
                            }
                            else
                            {
                                myType = types.FindAfterId(s.Type);
                                name = myType.Name;
                            }
                            act.Params.Add(name, (myType, bs));
                        }
                    }
                    Console.WriteLine(op.Key);
                }
            }
            var typesFromActionToTransformBlocks= actions

                .SelectMany(it => it.Params)
                .Select(param => param.Value.type)
                .Distinct()
                .Where(type => type.TranslateToBlocklyType() == null)

                .ToArray();

            var typesToTransfromBlocks = types
                .Where(t=>t != null)
                .Where(type => type.TranslateToBlocklyType() == null).ToArray();

            var remaining = typesToTransfromBlocks.Except(typesFromActionToTransformBlocks).ToArray();
            return actions;

        }
       

        #endregion
        /// <summary>
        /// The blockly tool box function definition
        /// </summary>
        public string BlocklyToolBoxFunctionDefinition;

        /// <summary>
        /// The blockly API functions
        /// </summary>
        public string BlocklyAPIFunctions;

        /// <summary>
        /// The blockly tool box value definition
        /// </summary>
        public string BlocklyToolBoxValueDefinition;

        /// <summary>
        /// The blockly types definition
        /// </summary>
        public string BlocklyTypesDefinition;

        private Timer _timer;
        


        private readonly IApiDescriptionGroupCollectionProvider api;

        /// <summary>
        /// Initializes a new instance of the <see cref="GenerateBlocklyFilesHostedService"/> class.
        /// </summary>
        /// <param name="api">The API.</param>
        public GenerateBlocklyFilesHostedService(IApiDescriptionGroupCollectionProvider api)
        {
            this.api = api;
            this.swaggers = new  Dictionary<string, BlocklyFileGenerator>();
        }
        /// <summary>
        /// starts
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        }
        private void DoWork(object state)
        {
           
            _timer.Dispose();
            var e = new EnumerateWebAPI(api);
            var actionList = e.CreateActionList();
            var blocklyFileGenerator = new BlocklyFileGenerator(actionList);
            BlocklyTypesDefinition = blocklyFileGenerator.GenerateNewBlocklyTypesDefinition();
            BlocklyAPIFunctions = blocklyFileGenerator.GenerateBlocklyAPIFunctions();
            BlocklyToolBoxValueDefinition = blocklyFileGenerator.GenerateBlocklyToolBoxValueDefinitionFile();
            BlocklyToolBoxFunctionDefinition = blocklyFileGenerator.GenerateBlocklyToolBoxFunctionDefinitionFile();
           
        }
        
        /// <summary>
        /// Triggered when the application host is performing a graceful shutdown.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
        public Task StopAsync(CancellationToken cancellationToken)
        {

            return Task.CompletedTask;
        }
    }
   
}
