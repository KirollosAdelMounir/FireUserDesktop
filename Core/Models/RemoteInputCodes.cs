namespace UserIdentityDesktop.Core.Models;

/// <summary>USB keyboard usages and the bounded extended-input wire contract.</summary>
public static class RemoteInputCodes
{
    public static bool IsKey(int code)=>code is >=4 and <=101 or >=103 and <=115 or >=224 and <=231;
    public static bool IsLegacyKey(int code)=>code is >=4 and <=56 or >=58 and <=69 or 76 or >=79 and <=82 or >=224 and <=231;
    public static bool RequiresExtension(RemoteInput input)=>input.Kind==6 || input.Kind==1&&input.Code>3 || input.Kind==2&&!IsLegacyKey(input.Code);
}
