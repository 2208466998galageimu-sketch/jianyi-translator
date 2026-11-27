using Thanslator.Translation;

namespace Thanslator.Tests;

public sealed class ModelStorageTests
{
    [Fact]
    public async Task MigrationCopiesVerifiedFiles_ResumesMatchingCopyAndRejectsOtherFiles()
    {
        var area=Path.Combine(Path.GetTempPath(),"jianyi-storage-"+Guid.NewGuid());var source=Path.Combine(area,"old");var target=Path.Combine(area,"new");
        Directory.CreateDirectory(Path.Combine(source,"ocr"));await File.WriteAllTextAsync(Path.Combine(source,"ocr","model.onnx"),"fixture-weights");
        try
        {
            await ModelStorage.CopyVerifiedAsync(source,target,[Path.Combine(source,"ocr")],null,default);
            Assert.Equal("fixture-weights",await File.ReadAllTextAsync(Path.Combine(target,"ocr","model.onnx")));
            Assert.True(File.Exists(Path.Combine(source,"ocr","model.onnx")));
            await Assert.ThrowsAsync<ArgumentException>(()=>ModelStorage.CopyVerifiedAsync(source,Path.Combine(source,"nested"),[Path.Combine(source,"ocr")],null,default));
            await ModelStorage.CopyVerifiedAsync(source,target,[Path.Combine(source,"ocr")],null,default);
            await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "unrelated");
            await Assert.ThrowsAsync<ArgumentException>(()=>ModelStorage.CopyVerifiedAsync(source,target,[Path.Combine(source,"ocr")],null,default));
            Assert.Equal("unrelated", await File.ReadAllTextAsync(Path.Combine(target, "keep.txt")));
            Assert.Throws<ArgumentException>(()=>ModelStorage.ManagedDirectory(source,source));
            Assert.Throws<ArgumentException>(()=>ModelStorage.ManagedDirectory(source,source+"-other"));
        }
        finally{Directory.Delete(area,true);}
    }

    [Fact]
    public async Task CancellationLeavesOriginalAndDoesNotReportMigrationSuccess()
    {
        var area=Path.Combine(Path.GetTempPath(),"jianyi-cancel-"+Guid.NewGuid());var source=Path.Combine(area,"old");Directory.CreateDirectory(Path.Combine(source,"ocr"));
        await File.WriteAllTextAsync(Path.Combine(source,"ocr","model.onnx"),"weights");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var target = Path.Combine(area, "new");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ModelStorage.CopyVerifiedAsync(source, target, [Path.Combine(source, "ocr")],
                    new CancelOnProgress(cancellation), cancellation.Token));
            Assert.True(File.Exists(Path.Combine(source, "ocr", "model.onnx")));
            Assert.False(Directory.Exists(target));
            Assert.Empty(Directory.EnumerateDirectories(area, ".jianyi-migration-*"));
        }
        finally{Directory.Delete(area,true);}
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<string>
    {
        public void Report(string value) => cancellation.Cancel();
    }
}
