using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Models;
using Portalkeeper.Services.CharacterRendering;

namespace Portalkeeper.Services;

public sealed record ArmoryPreviewResult(string? ImagePath,string Status,bool FromCache=false);

public sealed class ArmoryPreviewService
{
    // Serialize CPU rendering and StormLib use across all Armory windows.
    private static readonly SemaphoreSlim RenderGate=new(1,1);
    private readonly string _cache;
    public ArmoryPreviewService(string? cacheDirectory=null)
    {
        _cache=cacheDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Portalkeeper","armory-cache","previews");
    }
    public async Task<ArmoryPreviewResult> LoadAsync(ArmoryCharacter character,string realmUrl,string clientFolder,CancellationToken token, bool transmogSupported = false, bool showTransmog = true)
    {
        if(string.IsNullOrWhiteSpace(clientFolder))return new(null,"Choose your WoW client folder in Settings to show a character preview.");
        // Serialize only visual inputs: no timestamps/durability, so a periodic feed publish does not invalidate an identical preview.
        var visual=new {character.Race,character.Gender,character.Class,character.Appearance,
            transmogSupported, showTransmog,
            Equipment=character.Equipment.OrderBy(e=>e.Slot).Select(e=>new {e.Slot,e.Entry,e.DisplayId,e.InventoryType,e.ItemClass,e.Transmog})};
        string visualJson=JsonSerializer.Serialize(visual);
        return await Task.Run(async ()=>
        {
            string? temp=null;
            try
            {
                token.ThrowIfCancellationRequested();
                var fingerprint=ClientAssets.Fingerprint(clientFolder);
                var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("armory-render-v3|"+realmUrl+"|"+fingerprint+"|"+visualJson)));
                Directory.CreateDirectory(_cache);var target=Path.Combine(_cache,key+".png");
                bool ValidCache()
                {
                    try {using var bitmap=SkiaSharp.SKBitmap.Decode(target);return bitmap is {Width:480,Height:640};}catch{return false;}
                }
                if(File.Exists(target)&&ValidCache())return new ArmoryPreviewResult(target,"",true);
                await RenderGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if(File.Exists(target)&&ValidCache())return new ArmoryPreviewResult(target,"",true);
                    var bytes=CharacterRasterizer.Render(clientFolder,ArmoryVisualEquipment.Select(character, transmogSupported && showTransmog),token);
                    // Do not cache a result against archive metadata that changed while reading.
                    if(ClientAssets.Fingerprint(clientFolder)!=fingerprint)throw new IOException("The client files changed while creating this preview.");
                    token.ThrowIfCancellationRequested();temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
                    await File.WriteAllBytesAsync(temp,bytes,token).ConfigureAwait(false);File.Move(temp,target,true);temp=null;
                    // Bound the on-disk cache. Leave recent previews alone while other windows may display them.
                    foreach(var file in new DirectoryInfo(_cache).EnumerateFiles("*.png").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(200))
                        try {file.Delete();} catch(IOException) {} catch(UnauthorizedAccessException) {}
                    return new ArmoryPreviewResult(target,"");
                }
                finally {RenderGate.Release();}
            }
            catch(OperationCanceledException){throw;}
            catch(Exception ex)
            {
                string message=ex is DllNotFoundException or TypeInitializationException?"Character preview needs the native StormLib library.":
                    ex is DirectoryNotFoundException?"Local client files are unavailable. Check the client folder in Settings.":
                    ex is NotSupportedException?ex.Message:"Character preview is unavailable for these client assets. Equipment details are still available.";
                return new ArmoryPreviewResult(null,message);
            }
            finally {if(temp is not null)try{File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
        },token).ConfigureAwait(false);
    }
}
