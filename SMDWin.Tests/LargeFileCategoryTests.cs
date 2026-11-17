using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The categories in Storage › Large files are decided by the real folders, not by pieces of text in the path.
    public class LargeFileCategoryTests
    {
        public LargeFileCategoryTests()
        {
            LargeFileCategory.WindowsDir = @"c:\windows";
            LargeFileCategory.ProgramDirs = new[] { @"c:\program files", @"c:\program files (x86)" };
            LargeFileCategory.TempDirs = new[] { @"c:\users\paul\appdata\local\temp", @"c:\windows\temp" };
        }

        [Theory]
        [InlineData(@"C:\Windows\ServiceProfiles\NetworkService\content.bin", LargeFileCategory.SystemFiles)]
        [InlineData(@"C:\Program Files\BraveSoftware\Brave-Browser\chrome.7z", LargeFileCategory.Apps)]
        [InlineData(@"C:\Users\Paul\.vscode\extensions\openai.chatgpt-26\bin\windows-x86_64\codex.exe", LargeFileCategory.Other)]
        [InlineData(@"C:\Users\Paul\AppData\Local\Temp\setup.cab", LargeFileCategory.Temporary)]
        [InlineData(@"C:\Users\Paul\Videos\SDx_crash.log", LargeFileCategory.Logs)]
        [InlineData(@"C:\Users\Paul\Videos\SDx_crash.log.old", LargeFileCategory.Logs)]
        [InlineData(@"C:\Users\Paul\Videos\holiday.mp4", LargeFileCategory.Media)]
        [InlineData(@"D:\Backups\disk.img", LargeFileCategory.Backup)]
        [InlineData(@"C:\WindowsApps\x.bin", LargeFileCategory.Other)]
        public void Classify(string path, string expected) => Assert.Equal(expected, LargeFileCategory.Classify(path));

        [Fact]
        public void OnlyTemporaryIsSafeToDelete()
        {
            Assert.True(LargeFileCategory.IsSafeToDelete(LargeFileCategory.Temporary));
            Assert.False(LargeFileCategory.IsSafeToDelete(LargeFileCategory.Logs));
            Assert.False(LargeFileCategory.IsSafeToDelete(LargeFileCategory.SystemFiles));
        }
    }
}
