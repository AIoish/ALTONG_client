using System.IO;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class QuickMemoStoreTests
{
    [TestMethod]
    public void SessionDrafts_AreIsolatedAndSurviveReopeningAndSavingAnotherSession()
    {
        WithPath(path =>
        {
            var store = new QuickMemoStore(path);
            store.Save("첫 집중에서 저장한 메모", "first");
            store.SaveDraft("첫 집중의 초안", "first");
            Assert.AreEqual("", store.GetDraftForSession("second"));
            Assert.AreEqual(0, store.GetSavedForSession("second").Count);
            store.SaveDraft("두 번째 집중의 초안", "second");
            store.Save("두 번째 집중의 메모", "second");
            var reopened = new QuickMemoStore(path);
            Assert.AreEqual("첫 집중의 초안", reopened.GetDraftForSession("first"));
            Assert.AreEqual("", reopened.GetDraftForSession("second"));
            Assert.AreEqual("", reopened.GetDraftForSession(null));
            Assert.AreEqual("첫 집중에서 저장한 메모", reopened.GetSavedForSession("first").Single().Text);
            Assert.AreEqual("두 번째 집중의 메모", reopened.GetSavedForSession("second").Single().Text);
            Assert.AreEqual(2, reopened.GetForSession("first", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Count);
        });
    }

    [TestMethod]
    public void ReportSelection_UsesSessionOwnership_AndMatchesOnlyUntaggedNotesInsideThePeriod()
    {
        var store = new QuickMemoStore();
        var start = DateTime.UtcNow.AddMinutes(-1);
        store.Save("이 세션", "session-one");
        store.Save("다른 세션", "session-two");
        store.Save("이전 버전에서 저장한 메모");
        store.SaveDraft("미저장 초안", "session-one");
        var end = DateTime.UtcNow.AddMinutes(1);
        var selected = store.GetForSession("session-one", start, end);
        CollectionAssert.AreEquivalent(new[] { "이 세션", "이전 버전에서 저장한 메모", "미저장 초안" }, selected.Select(m => m.Text).ToArray());
        Assert.IsTrue(selected.Single(m => m.Text == "미저장 초안").IsDraft);
        Assert.AreEqual("미저장 초안", store.Draft, "Displaying a report must not consume the original draft.");
        Assert.AreEqual(0, store.GetForSession("unrelated", end, end.AddMinutes(1)).Count);
    }

    [TestMethod]
    public void DraftAndSavedMemos_SurviveReopeningAndKeepTheirTimestamp()
    {
        WithPath(path =>
        {
            var store = new QuickMemoStore(path);
            store.SaveDraft("한글 초안\n다음에 물어볼 내용");
            var reopened = new QuickMemoStore(path);
            Assert.AreEqual(store.Draft, reopened.Draft);
            Assert.IsNull(reopened.Save(" \n "));
            var saved = reopened.Save(reopened.Draft)!;
            var afterSave = new QuickMemoStore(path);
            Assert.AreEqual("", afterSave.Draft);
            Assert.AreEqual(saved, afterSave.Entries.Single());
            afterSave.SaveDraft("다음 집중에서도 남을 초안");
            Assert.AreEqual(saved, new QuickMemoStore(path).Entries.Single());
        });
    }

    [TestMethod]
    public void FailedWrite_PreservesSavedFileAndDraft_AndCanBeRetried()
    {
        WithPath(path =>
        {
            var store = new QuickMemoStore(path);
            store.Save("이전 메모");
            store.SaveDraft("저장할 내용");
            string original = File.ReadAllText(path);
            File.Move(path, path + ".backup");
            Directory.CreateDirectory(path);
            try
            {
                try { store.Save(store.Draft); Assert.Fail("The blocked destination must fail."); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Assert.AreEqual("저장할 내용", store.Draft);
                Assert.AreEqual(1, store.Entries.Count);
                Assert.AreEqual(original, File.ReadAllText(path + ".backup"));
            }
            finally { Directory.Delete(path); File.Move(path + ".backup", path); }
            store.Save(store.Draft);
            var reopened = new QuickMemoStore(path);
            Assert.AreEqual(2, reopened.Entries.Count);
            Assert.AreEqual("", reopened.Draft);
        });
    }

    [TestMethod]
    public void UnreadableFile_IsNeverOverwritten_AndReloadCanRecover()
    {
        WithPath(path =>
        {
            File.WriteAllText(path, "{broken");
            var store = new QuickMemoStore(path);
            Assert.IsNotNull(store.LoadError);
            Assert.ThrowsException<IOException>(() => store.SaveDraft("new"));
            Assert.ThrowsException<IOException>(() => store.Save("new"));
            Assert.AreEqual("{broken", File.ReadAllText(path));
            File.Delete(path);
            var repaired = new QuickMemoStore(path);
            repaired.Save("복구된 메모");
            store.Reload();
            Assert.IsNull(store.LoadError);
            Assert.AreEqual("복구된 메모", store.Entries.Single().Text);
        });
    }

    private static void WithPath(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "altong-memo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "memos.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
