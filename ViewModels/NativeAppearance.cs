using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Styling;
namespace UserIdentityDesktop.ViewModels;
public static class NativeAppearance
{
    private static readonly (string Key,string Dark,string Light)[] Colors = [
        ("ForgeBg","#121316","#F5F4F0"),("ForgeBgSecondary","#18191E","#EBE8E2"),
        ("ForgeSurface","#1C1D23","#FFFFFF"),("ForgeSurfaceCard","#1A1B21","#FFFFFF"),
        ("ForgeSurfaceHover","#262830","#F7F4EF"),("ForgeBubbleReceived","#22242B","#FFFFFF"),
        ("ForgeBubbleSent","#3D2C25","#FFF0E6"),("ForgeBubbleSentBorder","#66503A","#F0D5C0")];
    public static Dictionary<string,Color> Palette(int level)
    {
        var t=Math.Clamp(level,0,100)/100d;var result=new Dictionary<string,Color>();
        foreach(var (key,dark,light) in Colors)result[key]=Mix(Color.Parse(dark),Color.Parse(light),t);
        var bg=result["ForgeBg"];var ink=Contrast(Avalonia.Media.Colors.White,bg)>=Contrast(Avalonia.Media.Colors.Black,bg)?Avalonia.Media.Colors.White:Avalonia.Media.Colors.Black;
        var target=ink==Avalonia.Media.Colors.White?Avalonia.Media.Colors.Black:Avalonia.Media.Colors.White;
        foreach(var (key,_,_) in Colors)if(key!="ForgeBubbleSentBorder"){
            var start=result[key];for(var i=0;i<=100&&Contrast(ink,result[key])<4.6;i++)result[key]=Mix(start,target,i/100d);
        }
        result["ForgeTextPrimary"]=ink;
        foreach(var (key,amount) in new[]{("ForgeTextSecondary",.12),("ForgeTextMuted",.20)}){
            var start=Mix(ink,result["ForgeBg"],amount);var minimum=key=="ForgeTextMuted"?3:4.5;
            for(var i=0;i<=100;i++){
                var candidate=Mix(start,ink,i/100d);var valid=true;
                foreach(var (surface,_,_) in Colors)if(surface!="ForgeBubbleSentBorder"&&Contrast(candidate,result[surface])<minimum)valid=false;
                if(valid){result[key]=candidate;break;}
            }
        }
        result["ForgeBorder"]=Mix(result["ForgeBg"],ink,.22);
        var primary=Color.Parse(level<50?"#DD7036":"#E0531C");
        for(var i=0;i<=100;i++){
            var candidate=Mix(primary,ink,i/100d);var valid=true;
            foreach(var (key,_,_) in Colors)if(key!="ForgeBubbleSentBorder"&&Contrast(candidate,result[key])<3)valid=false;
            if(valid){primary=candidate;break;}
        }
        result["ForgePrimary"]=primary;result["ForgePrimaryHover"]=primary;result["ForgeBorderFocus"]=primary;
        return result;
    }
    public static double Contrast(Color a,Color b){var x=Luminance(a);var y=Luminance(b);return(Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
    private static double Luminance(Color c){static double Channel(byte b){var v=b/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}return Channel(c.R)*.2126+Channel(c.G)*.7152+Channel(c.B)*.0722;}
    private static Color Mix(Color a,Color b,double t)=>Color.FromRgb((byte)Math.Round(a.R+(b.R-a.R)*t),(byte)Math.Round(a.G+(b.G-a.G)*t),(byte)Math.Round(a.B+(b.B-a.B)*t));
    public static void Apply(int level)
    {
        if(Avalonia.Application.Current is not {} app)return;
        var palette=Palette(level);
        app.RequestedThemeVariant=palette["ForgeTextPrimary"]==Avalonia.Media.Colors.White?ThemeVariant.Dark:ThemeVariant.Light;
        foreach(var (key,color) in palette)app.Resources[key]=new SolidColorBrush(color);
    }
}
