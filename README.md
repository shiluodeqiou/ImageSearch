# ImageSearch
<a href="https://gitee.com/masuit/ImageSearch"><img src="https://gitee.com/static/images/logo-black.svg" height="24"></a>
<a href="https://github.com/ldqk/ImageSearch"><img src="https://upload.wikimedia.org/wikipedia/commons/thumb/9/95/Font_Awesome_5_brands_github.svg/54px-Font_Awesome_5_brands_github.svg.png" height="24"><img src="https://upload.wikimedia.org/wikipedia/commons/thumb/2/29/GitHub_logo_2013.svg/128px-GitHub_logo_2013.svg.png" height="24"></a>

图片exif信息移除小工具和本地硬盘以图搜图案例Demo分享，灵感来源于[DuplicateCleaner](https://masuit.org/1776)，**千万级图片秒级检索**：   
<img width="1165" height="840" alt="image" src="https://github.com/user-attachments/assets/9f295f3b-3edf-4227-bbd8-a4b386b59251" />
<img width="1307" height="1040" alt="image" src="https://github.com/user-attachments/assets/68aefef0-b143-4385-a7f9-fb9dbcaf073d" />
<img width="1377" height="911" alt="image" src="https://github.com/user-attachments/assets/34a37f96-a665-43ef-a4c9-c4f3a63c8b0e" />


## 环境要求
开发环境：Visual Studio 2026  
运行时：.net10 desktop  

## 硬件要求
处理器：4核或更多  
内存：8GB或更多

## 特别说明
1. 如果电脑中安装有everything，软件会自动调取everything进行目录扫描，请确保要扫描的目录已经被everything索引，如果你想让软件不自动调取everything，把目录下的everything64.dll文件删掉即可
2. 软件不支持部分区域的图片检索，只能做相似检索
3. 相似度限定70是因为低于70的相似度肉眼看上去已经是完全不一样的图了

## 视频检索
支持对本地视频建立索引并检索，原理是抽取视频的**代表帧**（类似资源管理器的视频缩略图），
再按与图片完全相同的方式计算哈希——因此视频和图片共用同一份索引，可以互相搜到。

支持格式：mp4 / mov / avi / mkv / wmv / flv / m4v / mpg / mpeg / ts / webm / 3gp / rmvb

抽帧采用混合方案，按可用性自动选择，无需配置：
1. **ffmpeg（首选）**——独立进程，坏文件不会影响主程序，且可并行、格式覆盖最广。
   程序目录 `tools/ffmpeg.exe` 内置了一份 LGPL v3 构建（可随程序分发，仅用于解码）。
2. **Windows 系统缩略图（兜底）**——缺少 ffmpeg 时自动启用，零依赖。
   该接口非线程安全，程序内部已串行化处理。

自定义 ffmpeg：在 `config.ini` 中设置 `FFmpegPath`，或在系统 PATH 中提供 ffmpeg。
若不需要内置版本，删除 `以图搜图/tools/` 目录即可——程序仍可正常编译与运行，
只是视频抽帧退回到系统缩略图方案。

> 说明：微信等来源的短视频常短至 1~2 秒，且很多"扩展名为 .jpg、实际是 HEIC/视频"。
> 本工具以扩展名判断类型，若发现文件被错误跳过，可核对实际文件头。
## Star趋势

<img src="https://starchart.cc/ldqk/ImageSearch.svg">

## 理论篇
https://segmentfault.com/a/1190000038308093

## 特别鸣谢
[Masuit.Tools](https://github.com/ldqk/Masuit.Tools)

## 本项目完全开源，以下链接的为盗版，若您在以下链接以及相关链接有任何的付费行为，请申请退款或向相关平台方投诉
https://www.chinapyg.com/forum.php?mod=viewthread&tid=162510  
https://download.csdn.net/download/china365love/92183755  
https://blog.csdn.net/china365love/article/details/153752532  
https://shop.owmei.com/
