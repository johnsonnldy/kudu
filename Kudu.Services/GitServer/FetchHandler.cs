﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Kudu.Contracts.Infrastructure;
using Kudu.Contracts.Settings;
using Kudu.Contracts.SourceControl;
using Kudu.Contracts.Tracing;
using Kudu.Core.Deployment;
using Kudu.Core.SourceControl.Git;
using Newtonsoft.Json.Linq;
using Kudu.Core;
using Kudu.Core.Infrastructure;
using System.IO;

namespace Kudu.Services.GitServer
{
    public class FetchHandler : IHttpHandler
    {
        private readonly IGitServer _gitServer;
        private readonly IDeploymentManager _deploymentManager;
        private readonly IDeploymentSettingsManager _settings;
        private readonly ITracer _tracer;
        private readonly IOperationLock _deploymentLock;
        private readonly RepositoryConfiguration _configuration;
        private readonly IEnvironment _environment;

        public FetchHandler(ITracer tracer,
                            IGitServer gitServer,
                            IDeploymentManager deploymentManager,
                            IDeploymentSettingsManager settings,
                            IOperationLock deploymentLock,
                            RepositoryConfiguration configuration,
                            IEnvironment environment)
        {
            _gitServer = gitServer;
            _deploymentManager = deploymentManager;
            _settings = settings;
            _tracer = tracer;
            _deploymentLock = deploymentLock;
            _configuration = configuration;
            _environment = environment;
        }

        public bool IsReusable
        {
            get
            {
                return false;
            }
        }

        private string MarkerFilePath
        {
            get
            {
                return Path.Combine(_environment.DeploymentCachePath, "pending");
            }
        }

        public void ProcessRequest(HttpContext context)
        {
            using (_tracer.Step("FetchHandler"))
            {
                string json = context.Request.Form["payload"];

                context.Response.TrySkipIisCustomErrors = true;

                if (String.IsNullOrEmpty(json))
                {
                    _tracer.TraceWarning("Received empty json payload");
                    context.Response.StatusCode = 400;
                    context.ApplicationInstance.CompleteRequest();
                    return;
                }

                if (_configuration.TraceLevel > 1)
                {
                    TracePayload(json);
                }

                RepositoryInfo repositoryInfo = null;

                try
                {
                    repositoryInfo = GetRepositoryInfo(context.Request, json);
                }
                catch (FormatException ex)
                {
                    _tracer.TraceError(ex);
                    context.Response.StatusCode = 400;
                    context.Response.Write(ex.Message);
                    context.ApplicationInstance.CompleteRequest();
                    return;
                }

                string targetBranch = _settings.GetValue("branch") ?? "master";

                _tracer.Trace("Attempting to fetch target branch {0}", targetBranch);

                _deploymentLock.LockOperation(() =>
                {
                    PerformDeployment(repositoryInfo, targetBranch);
                },
                () =>
                {
                    // Create a marker file that indicates if there's another deployment to pull
                    // because there was a deployment in progress.
                    using (_tracer.Step("Creating maker file"))
                    {
                        // REVIEW: This makes the assumption that the repository url is the same.
                        // If it isn't the result would be buggy either way.
                        CreateMarkerFile();
                    }

                    context.Response.StatusCode = 409;
                    context.ApplicationInstance.CompleteRequest();
                });
            }
        }

        private void CreateMarkerFile()
        {
            File.WriteAllText(MarkerFilePath, String.Empty);
        }

        private bool MarkerFileExists()
        {
            return File.Exists(MarkerFilePath);
        }

        private void DeleteMarkerFile()
        {
            FileSystemHelpers.DeleteFileSafe(MarkerFilePath);
        }

        private void PerformDeployment(RepositoryInfo repositoryInfo, string targetBranch)
        {
            using (_tracer.Step("Performing fetch based deployment"))
            {
                _gitServer.Initialize(_configuration);
                _gitServer.SetReceiveInfo(repositoryInfo.OldRef, repositoryInfo.NewRef, targetBranch);
                _gitServer.FetchWithoutConflict(repositoryInfo.RepositoryUrl, "external", targetBranch);
                _deploymentManager.Deploy(repositoryInfo.Deployer);

                if (MarkerFileExists())
                {
                    using (_tracer.Step("Marker file exists"))
                    {
                        using (_tracer.Step("Deleting marker file"))
                        {
                            DeleteMarkerFile();
                        }

                        PerformDeployment(repositoryInfo, targetBranch);
                    }
                }
            }
        }

        private void TracePayload(string json)
        {
            var attribs = new Dictionary<string, string>
            {
                { "json", json }
            };

            _tracer.Trace("payload", attribs);
        }

        private RepositoryInfo GetRepositoryInfo(HttpRequest request, string json)
        {
            JObject payload = null;
            try
            {
                payload = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                throw new FormatException(Resources.Error_UnsupportedFormat, ex);
            }

            var info = new RepositoryInfo();

            // If it has a repository, then try to get information from that
            var repository = payload.Value<JObject>("repository");

            if (repository != null)
            {
                // Try to assume the github format
                // { repository: { url: "" }, ref: "", before: "", after: "" } 
                info.RepositoryUrl = repository.Value<string>("url");

                // The format of ref is refs/something/something else
                // For master it's normally refs/head/master
                string @ref = payload.Value<string>("ref");

                if (String.IsNullOrEmpty(@ref))
                {
                    throw new FormatException(Resources.Error_UnsupportedFormat);
                }

                // Just get the last token
                info.Branch = @ref.Split('/').Last();
                info.Deployer = GetDeployer(request);
                info.OldRef = payload.Value<string>("before");
                info.NewRef = payload.Value<string>("after");
            }
            else
            {
                // Look for the generic format
                // { url: "", branch: "", deployer: "", oldRef: "", newRef: "" } 
                info.RepositoryUrl = payload.Value<string>("url");
                info.Branch = payload.Value<string>("branch");
                info.Deployer = payload.Value<string>("deployer");
                info.OldRef = payload.Value<string>("oldRef");
                info.NewRef = payload.Value<string>("newRef");
            }

            // If there's no specified branch assume master
            if (String.IsNullOrEmpty(info.Branch))
            {
                // REVIEW: Is this correct
                info.Branch = "master";
            }

            if (String.IsNullOrEmpty(info.RepositoryUrl))
            {
                throw new FormatException(Resources.Error_MissingRepositoryUrl);
            }

            return info;
        }

        private string GetDeployer(HttpRequest httpRequest)
        {
            // This is kind of hacky, we should have a consistent way of figuring out who's pushing to us
            if (httpRequest.Headers["X-Github-Event"] != null)
            {
                return "github";
            }

            // Look for a specific header here
            return null;
        }

        private class RepositoryInfo
        {
            public string RepositoryUrl { get; set; }
            public string OldRef { get; set; }
            public string NewRef { get; set; }
            public string Branch { get; set; }
            public string Deployer { get; set; }
        }
    }
}
