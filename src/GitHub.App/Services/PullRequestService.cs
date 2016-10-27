using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using GitHub.Models;
using System.Reactive.Linq;
using Rothko;
using System.Text;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using GitHub.Primitives;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Reactive;
using System.Collections.Generic;
using LibGit2Sharp;
using PullRequest = Octokit.PullRequest;
using System.Diagnostics;

namespace GitHub.Services
{
    [NullGuard.NullGuard(NullGuard.ValidationFlags.None)]
    [Export(typeof(IPullRequestService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class PullRequestService : IPullRequestService
    {
        static readonly Regex InvalidBranchCharsRegex = new Regex(@"[^0-9A-Za-z\-]", RegexOptions.ECMAScript);
        static readonly Regex BranchCapture = new Regex(@"branch\.(?<branch>.+)\.ghfvs-pr", RegexOptions.ECMAScript);

        static readonly string[] TemplatePaths = new[]
        {
            "PULL_REQUEST_TEMPLATE.md",
            "PULL_REQUEST_TEMPLATE",
            ".github\\PULL_REQUEST_TEMPLATE.md",
            ".github\\PULL_REQUEST_TEMPLATE",
        };

        readonly IGitClient gitClient;
        readonly IGitService gitService;
        readonly IOperatingSystem os;
        readonly IUsageTracker usageTracker;

        [ImportingConstructor]
        public PullRequestService(IGitClient gitClient, IGitService gitService, IOperatingSystem os, IUsageTracker usageTracker)
        {
            this.gitClient = gitClient;
            this.gitService = gitService;
            this.os = os;
            this.usageTracker = usageTracker;
        }

        public IObservable<IPullRequestModel> CreatePullRequest(IRepositoryHost host,
            ILocalRepositoryModel sourceRepository, IRepositoryModel targetRepository,
            IBranch sourceBranch, IBranch targetBranch,
            string title, string body
        )
        {
            Extensions.Guard.ArgumentNotNull(host, nameof(host));
            Extensions.Guard.ArgumentNotNull(sourceRepository, nameof(sourceRepository));
            Extensions.Guard.ArgumentNotNull(targetRepository, nameof(targetRepository));
            Extensions.Guard.ArgumentNotNull(sourceBranch, nameof(sourceBranch));
            Extensions.Guard.ArgumentNotNull(targetBranch, nameof(targetBranch));
            Extensions.Guard.ArgumentNotNull(title, nameof(title));
            Extensions.Guard.ArgumentNotNull(body, nameof(body));

            return PushAndCreatePR(host, sourceRepository, targetRepository, sourceBranch, targetBranch, title, body).ToObservable();
        }

        public IObservable<string> GetPullRequestTemplate(ILocalRepositoryModel repository)
        {
            Extensions.Guard.ArgumentNotNull(repository, nameof(repository));

            return Observable.Defer(() =>
            {
                var paths = TemplatePaths.Select(x => Path.Combine(repository.LocalPath, x));

                foreach (var path in paths)
                {
                    if (os.File.Exists(path))
                    {
                        try { return Observable.Return(os.File.ReadAllText(path, Encoding.UTF8)); } catch { }
                    }
                }
                return Observable.Empty<string>();
            });
        }

        public IObservable<bool> IsCleanForCheckout(ILocalRepositoryModel repository)
        {
            var repo = gitService.GetRepository(repository.LocalPath);
            return Observable.Return(!repo.RetrieveStatus().IsDirty);
        }

        public IObservable<Unit> Pull(ILocalRepositoryModel repository)
        {
            return Observable.Defer(() =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);
                var refspec = string.Format(CultureInfo.InvariantCulture, "{0}:{0}", repo.Head.FriendlyName);
                return gitClient.Fetch(repo, "origin", new[] { refspec }).ToObservable();
            });
        }

        public IObservable<Unit> FetchAndCheckout(ILocalRepositoryModel repository, int pullRequestNumber, string localBranchName)
        {
            return DoFetchAndCheckout(repository, pullRequestNumber, localBranchName).ToObservable();
        }

        public IObservable<string> GetDefaultLocalBranchName(ILocalRepositoryModel repository, int pullRequestNumber, string pullRequestTitle)
        {
            return Observable.Defer(() =>
            {
                var initial = "pr/" + pullRequestNumber + "-" + GetSafeBranchName(pullRequestTitle);
                var current = initial;
                var repo = gitService.GetRepository(repository.LocalPath);
                var index = 2;

                while (repo.Branches[current] != null)
                {
                    current = initial + '-' + index++;
                }

                return Observable.Return(current);
            });
        }

        public IObservable<HistoryDivergence> CalculateHistoryDivergence(ILocalRepositoryModel repository, int pullRequestNumber)
        {
            return Observable.Defer(async () =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);

                EnsurePullRefSpecExists(repo);
                await gitClient.Fetch(repo, "origin");

                var pullRef = repo.Refs[$"refs/remotes/origin/pr/{pullRequestNumber}"] as DirectReference;
                var commit = pullRef?.Target as Commit;

                if (commit != null)
                {
                    var result = repo.ObjectDatabase.CalculateHistoryDivergence(repo.Head.Tip, commit);
                    return Observable.Return(result);
                }
                else
                {
                    throw new InvalidOperationException("Could not find pull request ref.");
                }
            });
        }

        public IObservable<IBranch> GetLocalBranches(ILocalRepositoryModel repository, IPullRequestModel pullRequest)
        {
            return Observable.Defer(() =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);
                var result = GetLocalBranchesInternal(repository, repo, pullRequest).Select(x => new BranchModel(x, repository));
                return result.ToObservable();
            });
        }

        public bool IsPullRequestFromFork(ILocalRepositoryModel repository, IPullRequestModel pullRequest)
        {
            return pullRequest.Head.RepositoryCloneUrl?.ToRepositoryUrl() != repository.CloneUrl.ToRepositoryUrl();
        }

        public IObservable<Unit> SwitchToBranch(ILocalRepositoryModel repository, IPullRequestModel pullRequest)
        {
            return Observable.Defer(async () =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);
                var branchName = GetLocalBranchesInternal(repository, repo, pullRequest).FirstOrDefault();

                Debug.Assert(branchName != null, "PullRequestService.SwitchToBranch called but no local branch found.");

                if (branchName != null)
                {
                    await gitClient.Fetch(repo, "origin");

                    var branch = repo.Branches[branchName];

                    if (branch == null)
                    {
                        var trackedBranchName = $"refs/remotes/origin/" + branchName;
                        var trackedBranch = repo.Branches[trackedBranchName];

                        if (trackedBranch != null)
                        {
                            branch = repo.CreateBranch(branchName, trackedBranch.Tip);
                            await gitClient.SetTrackingBranch(repo, branchName, trackedBranchName);
                        }
                        else
                        {
                            throw new InvalidOperationException($"Could not find branch '{trackedBranchName}'.");
                        }
                    }

                    await gitClient.Checkout(repo, branchName);
                }

                return Observable.Empty<Unit>();
            });
        }

        public IObservable<Unit> UnmarkLocalBranch(ILocalRepositoryModel repository)
        {
            return Observable.Defer(async () =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);
                var configKey = $"branch.{repo.Head.FriendlyName}.ghfvs-pr";
                await gitClient.UnsetConfig(repo, configKey);
                return Observable.Return(Unit.Default);
            });
        }

        public IObservable<string> ExtractFile(ILocalRepositoryModel repository, string commitSha, string fileName)
        {
            return Observable.Defer(async () =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);
                await gitClient.Fetch(repo, "origin");
                var result = await gitClient.ExtractFile(repo, commitSha, fileName);
                return Observable.Return(result);
            });
        }

        public IObservable<Tuple<string, string>> ExtractDiffFiles(ILocalRepositoryModel repository, IPullRequestModel pullRequest, string fileName)
        {
            return Observable.Defer(async () =>
            {
                var repo = gitService.GetRepository(repository.LocalPath);
                await gitClient.Fetch(repo, "origin");
                var left = await gitClient.ExtractFile(repo, pullRequest.Base.Sha, fileName);
                var right = await gitClient.ExtractFile(repo, pullRequest.Head.Sha, fileName);
                return Observable.Return(Tuple.Create(left, right));
            });
        }

        async Task DoFetchAndCheckout(ILocalRepositoryModel repository, int pullRequestNumber, string localBranchName)
        {
            var repo = gitService.GetRepository(repository.LocalPath);
            var configKey = $"branch.{localBranchName}.ghfvs-pr";
            await gitClient.Fetch(repo, "origin", new[] { $"refs/pull/{pullRequestNumber}/head:{localBranchName}" });
            await gitClient.Checkout(repo, localBranchName);
            await gitClient.SetConfig(repo, configKey, pullRequestNumber.ToString());
        }

        IEnumerable<string> GetLocalBranchesInternal(
            ILocalRepositoryModel localRepository,
            IRepository repository,
            IPullRequestModel pullRequest)
        {
            if (!IsPullRequestFromFork(localRepository, pullRequest))
            {
                return new[] { pullRequest.Head.Ref };
            }
            else
            {
                var pr = pullRequest.Number.ToString(CultureInfo.InvariantCulture);
                return repository.Config
                    .Select(x => new { Branch = BranchCapture.Match(x.Key).Groups["branch"].Value, Value = x.Value })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Branch) && x.Value == pr)
                    .Select(x => x.Branch);
            }
        }

        async Task<IPullRequestModel> PushAndCreatePR(IRepositoryHost host,
            ILocalRepositoryModel sourceRepository, IRepositoryModel targetRepository,
            IBranch sourceBranch, IBranch targetBranch,
            string title, string body)
        {
            var repo = await Task.Run(() => gitService.GetRepository(sourceRepository.LocalPath));
            var remote = await gitClient.GetHttpRemote(repo, "origin");
            await gitClient.Push(repo, sourceBranch.Name, remote.Name);

            if (!repo.Branches[sourceBranch.Name].IsTracking)
                await gitClient.SetTrackingBranch(repo, sourceBranch.Name, remote.Name);

            // delay things a bit to avoid a race between pushing a new branch and creating a PR on it
            if (!Splat.ModeDetector.Current.InUnitTestRunner().GetValueOrDefault())
                await Task.Delay(TimeSpan.FromSeconds(5));

            var ret = await host.ModelService.CreatePullRequest(sourceRepository, targetRepository, sourceBranch, targetBranch, title, body);
            usageTracker.IncrementUpstreamPullRequestCount();
            return ret;
        }

        static void EnsurePullRefSpecExists(IRepository repo)
        {
            var spec = "+refs/pull/*/head:refs/remotes/origin/pr/*";
            var origin = repo.Network.Remotes["origin"];
            var existing = origin.FetchRefSpecs.FirstOrDefault(x => x.Specification == spec);

            if (existing == null)
            {
                repo.Network.Remotes.Update(origin, x => x.FetchRefSpecs.Add(spec));
            }
        }

        static string GetSafeBranchName(string name)
        {
            var before = InvalidBranchCharsRegex.Replace(name, "-").TrimEnd('-');

            for (;;)
            {
                string after = before.Replace("--", "-");

                if (after == before)
                {
                    return before.ToLower(CultureInfo.CurrentCulture);
                }

                before = after;
            }
        }
    }
}