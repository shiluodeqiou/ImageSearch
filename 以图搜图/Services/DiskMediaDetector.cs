using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace 以图搜图.Services;

/// <summary>磁盘介质类型。</summary>
public enum DiskMediaType
{
    /// <summary>无法确定。</summary>
    Unknown,

    /// <summary>机械硬盘（旋转介质）。</summary>
    Hdd,

    /// <summary>固态硬盘（无寻道延迟）。</summary>
    Ssd
}

/// <summary>单个物理磁盘的介质信息。</summary>
/// <param name="Type">介质类型。</param>
/// <param name="Bus">总线类型名称，例如 NVMe / SATA / USB。</param>
public readonly record struct DiskMediaInfo(DiskMediaType Type, string Bus)
{
    public string TypeName => Type switch
    {
        DiskMediaType.Ssd => "SSD",
        DiskMediaType.Hdd => "HDD",
        _ => "Unknown"
    };
}

/// <summary>
/// 磁盘介质识别。
///
/// 原实现只检查磁盘型号字符串里是否含有 "SSD"，导致型号名不含 SSD 的 NVMe 固态
/// （如"致态 TiPlus7100"、"WD_BLACK SN850X"）被误判为机械盘，使 SSD 快速索引路径失效。
///
/// 现在按可靠性依次尝试：
/// 1. 现代存储 API MSFT_PhysicalDisk 的 MediaType（0=未指定,3=HDD,4=SSD,5=SCM）；
/// 2. IOCTL_STORAGE_QUERY_PROPERTY 查询"寻道惩罚"，无寻道即固态；
/// 3. 型号字符串启发式（保留原逻辑作为最后兜底）。
/// </summary>
public static class DiskMediaDetector
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const int PropertyStandardQuery = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    /// <summary>
    /// 枚举全部物理磁盘的介质信息，键为物理磁盘号（与 Win32_DiskDrive.Index 一致）。
    /// </summary>
    public static Dictionary<int, DiskMediaInfo> DetectAll()
    {
        var map = new Dictionary<int, DiskMediaInfo>();

        // 方式一：现代存储 API，最可靠
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\Microsoft\Windows\Storage"),
                new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"));

            foreach (ManagementObject disk in searcher.Get())
            {
                if (!int.TryParse(disk["DeviceId"]?.ToString(), out var id))
                {
                    continue;
                }

                var mediaType = ToInt(disk["MediaType"]);
                var busType = ToInt(disk["BusType"]);

                // MediaType: 3=HDD, 4=SSD, 5=SCM(存储级内存)
                var type = mediaType switch
                {
                    3 => DiskMediaType.Hdd,
                    4 or 5 => DiskMediaType.Ssd,
                    _ => DiskMediaType.Unknown
                };

                map[id] = new DiskMediaInfo(type, BusName(busType));
            }
        }
        catch
        {
            // 该命名空间在部分系统/精简版 Windows 上不可用，交由后续方式处理
        }

        // 方式二：对仍未确定的磁盘查询寻道惩罚
        foreach (var id in map.Where(kv => kv.Value.Type == DiskMediaType.Unknown).Select(kv => kv.Key).ToList())
        {
            var penalty = QueryIncursSeekPenalty(id);
            if (penalty.HasValue)
            {
                map[id] = new DiskMediaInfo(penalty.Value ? DiskMediaType.Hdd : DiskMediaType.Ssd, map[id].Bus);
            }
        }

        return map;
    }

    /// <summary>
    /// 解析指定盘符所在物理磁盘的介质信息。
    /// </summary>
    /// <param name="mediaByDisk"><see cref="DetectAll"/> 的结果。</param>
    /// <param name="driveLetter">盘符，例如 'C'。</param>
    public static (DiskMediaInfo Media, string Model, string DiskIndex) Resolve(
        Dictionary<int, DiskMediaInfo> mediaByDisk, char driveLetter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_LogicalDisk WHERE DeviceID = '{driveLetter}:'");

            foreach (ManagementObject logicalDisk in searcher.Get())
            {
                foreach (ManagementObject partition in logicalDisk.GetRelated("Win32_DiskPartition"))
                {
                    foreach (ManagementObject diskDrive in partition.GetRelated("Win32_DiskDrive"))
                    {
                        var model = diskDrive["Model"]?.ToString() ?? "Unknown";
                        var diskIndex = diskDrive["Index"]?.ToString() ?? "Unknown";

                        // 优先使用现代 API 的结果
                        if (int.TryParse(diskIndex, out var id) && mediaByDisk.TryGetValue(id, out var info) && info.Type != DiskMediaType.Unknown)
                        {
                            return (info, model, diskIndex);
                        }

                        // 兜底：型号名启发式
                        if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                        {
                            return (new DiskMediaInfo(DiskMediaType.Ssd, "Unknown"), model, diskIndex);
                        }

                        // 无法判定：按机械盘处理（走更保守的索引路径）
                        return (new DiskMediaInfo(DiskMediaType.Hdd, "Unknown"), model, diskIndex);
                    }
                }
            }
        }
        catch
        {
            // 忽略，返回未知
        }

        return (new DiskMediaInfo(DiskMediaType.Unknown, "Unknown"), "Unknown", "Unknown");
    }

    /// <summary>
    /// 查询物理磁盘是否具有寻道惩罚。返回 true 表示机械盘，false 表示固态，null 表示无法查询。
    /// </summary>
    private static bool? QueryIncursSeekPenalty(int diskNumber)
    {
        try
        {
            using var handle = CreateFile($@"\\.\PhysicalDrive{diskNumber}", 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return null;
            }

            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery
            };

            var size = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
            var input = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(query, input, false);
                var output = new byte[16];

                // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version(4) + Size(4) + IncursSeekPenalty(1)
                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, input, size, output, output.Length, out _, IntPtr.Zero))
                {
                    return null;
                }

                return BitConverter.ToBoolean(output, 8);
            }
            finally
            {
                Marshal.FreeHGlobal(input);
            }
        }
        catch
        {
            return null;
        }
    }

    private static int ToInt(object? value)
    {
        return value == null ? -1 : Convert.ToInt32(value);
    }

    private static string BusName(int busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        17 => "NVMe",
        18 => "SCM",
        _ => busType < 0 ? "Unknown" : $"Bus{busType}"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
