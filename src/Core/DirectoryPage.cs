using System;
using System.IO;
using System.Linq;

namespace Qingye.Core
{
    public sealed class DirectoryPage
    {
        public const int PageSize=120;
        public string[] Directories,Files;
        public bool MoreDirectories,MoreFiles;

        public static DirectoryPage Read(string path,int directoryOffset,int fileOffset,bool readDirectories=true,bool readFiles=true)
        {
            string[] directories=readDirectories?Directory.EnumerateDirectories(path).Where(VisibleDirectory).Skip(directoryOffset).Take(PageSize+1).ToArray():new string[0];
            string[] files=readFiles?Directory.EnumerateFiles(path).Skip(fileOffset).Take(PageSize+1).ToArray():new string[0];
            return new DirectoryPage{Directories=directories.Take(PageSize).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray(),Files=files.Take(PageSize).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray(),MoreDirectories=directories.Length>PageSize,MoreFiles=files.Length>PageSize};
        }

        static bool VisibleDirectory(string path)
        {
            string name=Path.GetFileName(path);if(name==".git" || name=="node_modules" || name==".venv")return false;
            try{return (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0;}catch{return false;}
        }
    }
}
