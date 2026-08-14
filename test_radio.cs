using System;
using System.Threading.Tasks;
using Windows.Devices.Radios;

class Program {
    static void Main() {
        Run().GetAwaiter().GetResult();
    }
    static async Task Run() {
        var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
        Console.WriteLine("Access: " + access);
        var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
        Console.WriteLine("Radios: " + radios.Count);
        foreach(var r in radios) {
            Console.WriteLine("Name: " + r.Name + " Kind: " + r.Kind);
        }
    }
}
