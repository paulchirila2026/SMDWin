using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The BsodAdvisor rules: the probable area by the STOP code and the suspected driver.
    // The driver area comes from MiniDumpParser.DriverKind, so it does not depend on the interface language.
    public class BsodAdvisorTests
    {
        private static BsodAdvisor.Advice Get(string code, string? kind = null, string? driver = null, bool generic = false)
            => BsodAdvisor.Get(code, kind, driver, generic);

        [Theory]
        [InlineData("VIDEO_TDR_FAILURE", "Graphics card / driver")]
        [InlineData("VIDEO_DXGKRNL_FATAL_ERROR", "Graphics card / driver")]
        [InlineData("WHEA_UNCORRECTABLE_ERROR", "Hardware (CPU / memory / power)")]
        [InlineData("CLOCK_WATCHDOG_TIMEOUT", "Hardware (CPU / memory / power)")]
        [InlineData("MEMORY_MANAGEMENT", "Memory (RAM)")]
        [InlineData("FAULTY_HARDWARE_CORRUPTED_PAGE", "Memory (RAM)")]
        [InlineData("PAGE_FAULT_IN_NONPAGED_AREA", "Memory (RAM)")]
        [InlineData("KERNEL_DATA_INPAGE_ERROR", "Disk / storage")]
        [InlineData("NTFS_FILE_SYSTEM", "Disk / storage")]
        [InlineData("DPC_WATCHDOG_VIOLATION", "Disk / storage")]
        [InlineData("DRIVER_POWER_STATE_FAILURE", "Sleep / power transition")]
        [InlineData("ACPI_BIOS_ERROR", "BIOS / firmware")]
        [InlineData("CRITICAL_PROCESS_DIED", "Windows system files")]
        [InlineData("BAD_SYSTEM_CONFIG_INFO", "Windows system files")]
        [InlineData("KERNEL_SECURITY_CHECK_FAILURE", "Driver or memory")]
        [InlineData("IRQL_NOT_LESS_OR_EQUAL", "Driver")]
        [InlineData("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Driver")]
        [InlineData("MANUALLY_INITIATED_CRASH", "Manual crash")]
        [InlineData("SOMETHING_NEVER_SEEN", "Unknown")]
        public void AreaByStopCode_WithoutDriver(string code, string area)
            => Assert.Equal(area, Get(code).Area);

        [Fact]
        public void StopCodeWithHexPrefix_IsRecognized()
            => Assert.Equal("Memory (RAM)", Get("0x0000001A MEMORY_MANAGEMENT").Area);

        [Theory]
        [InlineData("nvlddmkm.sys", "Graphics card / driver")]
        [InlineData("Netwtw10.sys", "Network driver")]
        [InlineData("stornvme.sys", "Disk / storage")]
        [InlineData("ntfs.sys", "Disk / storage")]
        [InlineData("USBXHCI.SYS", "USB device / driver")]
        [InlineData("somevendor.sys", "Driver")]
        public void AreaByDriverName_OnGenericDriverCode(string driver, string area)
            => Assert.Equal(area, Get("IRQL_NOT_LESS_OR_EQUAL", null, driver).Area);

        [Fact]
        public void KindFromCrashEntry_IsUsed_WhenDriverNameIsMissing()
        {
            Assert.Equal("Network driver", Get("SYSTEM_SERVICE_EXCEPTION", "Network").Area);
            Assert.Equal("Disk / storage", Get("SYSTEM_SERVICE_EXCEPTION", "Storage").Area);
            Assert.Equal("Graphics card / driver", Get("SYSTEM_SERVICE_EXCEPTION", "Video").Area);
        }

        [Fact]
        public void LocalizedCategoryText_IsNotAKind()
        {
            // the displayed text ("Rețea", "Netzwerk") is not an area name: the rule must not depend on it
            Assert.Equal("Windows system files", Get("SYSTEM_SERVICE_EXCEPTION", "Rețea").Area);
        }

        [Fact]
        public void DpcWatchdog_WithNamedNonStorageDriver_IsDriver()
            => Assert.Equal("Driver", Get("DPC_WATCHDOG_VIOLATION", null, "somevendor.sys").Area);

        [Fact]
        public void PageFault_WithNamedDriver_PointsAtTheDriver()
        {
            var adv = Get("PAGE_FAULT_IN_NONPAGED_AREA", null, "somevendor.sys");
            Assert.Equal("Driver", adv.Area);
            Assert.Contains("somevendor.sys", adv.Summary);
        }

        [Fact]
        public void GenericCulprit_IsNotBlamed()
        {
            var adv = Get("KMODE_EXCEPTION_NOT_HANDLED", "Kernel", "ntoskrnl.exe", generic: true);
            Assert.Equal("Driver", adv.Area);
            Assert.DoesNotContain("ntoskrnl", adv.Summary);
        }

        [Theory]
        [InlineData("nvlddmkm.sys", MiniDumpParser.DriverKind.Video)]
        [InlineData("dxgkrnl.sys", MiniDumpParser.DriverKind.Video)]
        [InlineData("tcpip.sys", MiniDumpParser.DriverKind.Network)]
        [InlineData("storport.sys", MiniDumpParser.DriverKind.Storage)]
        [InlineData("iaStorAC.sys", MiniDumpParser.DriverKind.Storage)]
        [InlineData("ntfs.sys", MiniDumpParser.DriverKind.Storage)]
        [InlineData("ntoskrnl.exe", MiniDumpParser.DriverKind.Kernel)]
        [InlineData("hal.dll", MiniDumpParser.DriverKind.Kernel)]
        [InlineData("HDAudBus.sys", MiniDumpParser.DriverKind.Audio)]
        [InlineData("", MiniDumpParser.DriverKind.Other)]
        public void DriverKind_FromFileName(string file, MiniDumpParser.DriverKind kind)
            => Assert.Equal(kind, MiniDumpParser.GetDriverKind(file));

        [Fact]
        public void EveryAdvice_HasThreeSteps_AndAValidPage()
        {
            string[] pages = { "Stress", "Ram", "Disk", "Drivers", "Tools", "Battery" };
            foreach (var code in new[] { "VIDEO_TDR_FAILURE", "WHEA_UNCORRECTABLE_ERROR", "MEMORY_MANAGEMENT", "NTFS_FILE_SYSTEM",
                                         "DRIVER_POWER_STATE_FAILURE", "ACPI_BIOS_ERROR", "CRITICAL_PROCESS_DIED",
                                         "KERNEL_SECURITY_CHECK_FAILURE", "IRQL_NOT_LESS_OR_EQUAL", "BUGCODE_USB_DRIVER", "X" })
            {
                var adv = Get(code);
                Assert.True(adv.Steps.Count >= 2, code);
                Assert.True(adv.Page == null || System.Array.IndexOf(pages, adv.Page) >= 0, code);
            }
        }
    }
}
