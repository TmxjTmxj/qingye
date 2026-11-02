using System;
namespace Qingye.Core { public static class DirectoryPolicy {
public static bool ShouldSkipDirectory(string name) { return Array.Exists(new[]{".git",".svn","node_modules",".venv","bin","obj","packages","agent-history","agent-data","harness-data"},value=>String.Equals(value,name,StringComparison.OrdinalIgnoreCase)); }
} }
