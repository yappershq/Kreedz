namespace Kreedz.Shared;

/// <summary>
/// Stable movement-mode indices for record keying (cs2kz keys records on mode). String ids are the
/// registry ids; unknown ids fold to CKZ so a missing mode plugin can never split the record table.
/// </summary>
public static class KzModes
{
    public const int Ckz = 0;
    public const int Vnl = 1;

    public static int ToIndex(string id) => id switch
    {
        "vnl" => Vnl,
        _     => Ckz,
    };
}
