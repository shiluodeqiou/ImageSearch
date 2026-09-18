using System.Runtime.InteropServices;
using System.Text;
using 以图搜图.Models;

namespace 以图搜图;

public static class EverythingHelper
{
  /// <summary>
  /// Everything SDK 是进程级全局状态：SetSearch/Query/读结果共用同一查询上下文，
  /// 并发调用会互相覆盖结果。所有入口（含 EverythingFileIndex）必须持此锁。
  /// </summary>
  internal static readonly object SyncRoot = new();

  // 导入Everything DLL的方法
  [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
  private static extern uint Everything_SetSearch(string lpSearchString);

  [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
  private static extern void Everything_GetResultFullPathName(uint index, StringBuilder path, uint length);

  [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
  private static extern bool Everything_Query(bool wait);

  [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
  private static extern uint Everything_GetNumResults();

  [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
  private static extern void Everything_SetMax(uint dwMaxResults);

  static EverythingHelper()
  {
    Everything_SetMax(uint.MaxValue);
  }

  /// <summary>
  /// 用 Everything 枚举指定目录下的媒体文件。
  /// 默认过滤串同时包含图片与视频格式。
  /// </summary>
  public static IEnumerable<string> EnumerateFiles(string directoryPath, string extFilter = MediaFormats.EverythingFilterConst)
  {
    string search = $"file:\"{directoryPath}\" ext:{extFilter}"; // 仅文件，并限制路径

    // 持锁完成「查询 + 取结果」全过程。
    // 不能用 yield 迭代器包锁——MoveNext 之间锁会释放，其他线程可中途改查询。
    lock (SyncRoot)
    {
      Everything_SetSearch(search);
      Everything_Query(true); // 执行搜索
      uint numResults = Everything_GetNumResults();
      StringBuilder path = new StringBuilder(4096); // 根据需要调整长度

      var results = new List<string>((int)Math.Min(numResults, int.MaxValue));
      for (uint i = 0; i < numResults; i++)
      {
        Everything_GetResultFullPathName(i, path, 4096);
        results.Add(path.ToString());
      }

      return results;
    }
  }
}
