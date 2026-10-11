using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class ManualIssueEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manual-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly MediaDashDb _db;
    private readonly IssueStore _issues;
    private readonly MediaDashController _controller;
    private readonly Video _item;
    private readonly ProtectionService _protection;

    public ManualIssueEndpointTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        _issues = new IssueStore(_db.ConnectionFactory);
        _item = new Video { Id = Guid.NewGuid(), Path = Path.Combine(_root, "movie.mkv") };
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, args) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = new[] { _root } } },
            "GetItemList" => Array.Empty<BaseItem>(),
            "FindByPath" => Equals(args![0], _item.Path) ? _item : null,
            _ => null
        };
        _protection = new ProtectionService(_db.Protection, manager);
        _controller = new MediaDashController(_db, _issues, null!, null!, null!, null!, manager, null!,
            [], [], null!, new LibraryGuard(manager), new RunState(), null!, null!, _protection);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private ActionResult<object> Add(string? path = null) => _controller.CreateManualIssue(
        new ManualIssueRequest { Path = path ?? _item.Path, Type = ManualIssueType.Quality });

    [Fact]
    public void AddedIssueHasRealId_QueuedRetryKeepsSameIssue()
    {
        Assert.Equal(201, Assert.IsType<ObjectResult>(Add().Result).StatusCode);
        var issue = Assert.Single(_issues.GetIssues(type: IssueType.Quality));
        Assert.True(issue.Id > 0);
        _issues.BulkUpdateOpenIssueStatus(new[] { issue.Id }, IssueStatus.Queued);
        Assert.Equal(201, Assert.IsType<ObjectResult>(Add().Result).StatusCode);
        Assert.Equal(issue.Id, Assert.Single(_issues.GetIssues(type: IssueType.Quality)).Id);
    }

    [Fact]
    public void DismissedIssueReturnsConflictInsteadOfFalseSuccess()
    {
        Add();
        var issue = Assert.Single(_issues.GetIssues(type: IssueType.Quality));
        _issues.BulkUpdateOpenIssueStatus(new[] { issue.Id }, IssueStatus.Dismissed);
        Assert.IsType<ConflictObjectResult>(Add().Result);
        Assert.Equal(IssueStatus.Dismissed, Assert.Single(_issues.GetIssues(type: IssueType.Quality, status: null)).Status);
    }

    [Fact]
    public void ProtectedFileCannotBeManuallyQueued()
    {
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = _item.Path, FixersMask = "*", ScannersMask = "*" });
        _protection.Invalidate();
        Assert.IsType<ConflictObjectResult>(Add().Result);
        Assert.Empty(_issues.GetIssues(type: IssueType.Quality));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuickProtectPersistsFileOrFolderAndRemovingItRestoresEligibility(bool folder)
    {
        var target = folder ? _root : _item.Path;
        Assert.False(_protection.IsFixerBlocked(_item.Path, IssueType.Quality));
        var result = _controller.CreateProtection(new ProtectionRuleDto
        {
            Kind = "folder", Target = target, ScannersMask = "*", FixersMask = "*", Note = "Keep this"
        });
        var rule = Assert.IsType<ProtectionRuleDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(rule.Id > 0);
        Assert.True(_protection.IsScannerBlocked(_item.Path, IssueType.Quality));
        Assert.True(_protection.IsFixerBlocked(_item.Path, IssueType.Quality));
        Assert.IsType<ConflictObjectResult>(Add().Result);
        _controller.DeleteProtection(rule.Id);
        Assert.False(_protection.IsScannerBlocked(_item.Path, IssueType.Quality));
        Assert.False(_protection.IsFixerBlocked(_item.Path, IssueType.Quality));
        Assert.Equal(201, Assert.IsType<ObjectResult>(Add().Result).StatusCode);
    }

    [Theory]
    [InlineData("relative.mkv")]
    [InlineData("invalid\0.mkv")]
    public void InvalidPathIsBadRequest(string path) => Assert.IsType<BadRequestObjectResult>(Add(path).Result);

    [Fact]
    public void UnindexedFileReturnsActionableNotFound() => Assert.IsType<NotFoundObjectResult>(Add(Path.Combine(_root, "missing.mkv")).Result);
}
