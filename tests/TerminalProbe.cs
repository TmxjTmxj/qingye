using System;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
namespace Qingye.Tests {
public static class TerminalProbe {
[DllImport("kernel32.dll")]static extern IntPtr GetStdHandle(int id);
[DllImport("kernel32.dll")]static extern bool GetConsoleMode(IntPtr h,out uint mode);
[DllImport("kernel32.dll")]static extern bool SetConsoleMode(IntPtr h,uint mode);
public static int Main(string[] args){Console.OutputEncoding=new UTF8Encoding(false);uint mode;var handle=GetStdHandle(-11);if(GetConsoleMode(handle,out mode))SetConsoleMode(handle,mode|4);string esc="\x1b";
Console.Write(esc+"[3J"+esc+"[2J"+esc+"[H");
Console.Write("QY_COLOR "+esc+"[1;31mRED"+esc+"[0m\r\n");
Console.Write("QY_UNICODE e\u0301 中 👩‍💻 ░▒▓█ ┌─┐\r\n");
Console.Write("QY_SIZE "+Console.WindowWidth+"x"+Console.WindowHeight+"\r\n");
Console.Write("PRIMARY_SENTINEL\r\n");
if(args.Length>0&&args[0]=="alt"){
Console.Write(esc+"[?1049h"+esc+"[2J"+esc+"[H"+esc+"[36m┌─ Interactive screen ─┐"+esc+"[0m\r\n");
for(int i=0;i<6;i++){Console.Write(esc+"[3;1HFrame "+i+" 中 👩‍💻"+esc+"[K");Thread.Sleep(50);}Console.Write(esc+"[5;1HALT_READY");Console.ReadLine();Console.Write(esc+"[?1049l");}
Console.Write("\r\nPROBE_DONE\r\n");return 0;
} } }
