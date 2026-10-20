using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Loader
{
    /// <summary>
    /// Ribbon buttons point at these loader classes, never at the payload: Revit loads a button's assembly by path, which
    /// would load a second, unhosted copy of the payload (and ask about it as an unsigned add-in). Each slot is mapped at
    /// run time to a payload command class by name (LoaderApp.BindCommand). Manual transaction mode, so a payload command
    /// may open its own transactions; read-only commands simply don't.
    /// </summary>
    public abstract class ProxyCommand : IExternalCommand
    {
        protected abstract int Slot { get; }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            LoaderApp.RunCommand(Slot, commandData, ref message, elements);
    }

    [Transaction(TransactionMode.Manual)] public sealed class Cmd00 : ProxyCommand { protected override int Slot => 0; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd01 : ProxyCommand { protected override int Slot => 1; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd02 : ProxyCommand { protected override int Slot => 2; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd03 : ProxyCommand { protected override int Slot => 3; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd04 : ProxyCommand { protected override int Slot => 4; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd05 : ProxyCommand { protected override int Slot => 5; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd06 : ProxyCommand { protected override int Slot => 6; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd07 : ProxyCommand { protected override int Slot => 7; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd08 : ProxyCommand { protected override int Slot => 8; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd09 : ProxyCommand { protected override int Slot => 9; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd10 : ProxyCommand { protected override int Slot => 10; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd11 : ProxyCommand { protected override int Slot => 11; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd12 : ProxyCommand { protected override int Slot => 12; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd13 : ProxyCommand { protected override int Slot => 13; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd14 : ProxyCommand { protected override int Slot => 14; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd15 : ProxyCommand { protected override int Slot => 15; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd16 : ProxyCommand { protected override int Slot => 16; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd17 : ProxyCommand { protected override int Slot => 17; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd18 : ProxyCommand { protected override int Slot => 18; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd19 : ProxyCommand { protected override int Slot => 19; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd20 : ProxyCommand { protected override int Slot => 20; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd21 : ProxyCommand { protected override int Slot => 21; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd22 : ProxyCommand { protected override int Slot => 22; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd23 : ProxyCommand { protected override int Slot => 23; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd24 : ProxyCommand { protected override int Slot => 24; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd25 : ProxyCommand { protected override int Slot => 25; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd26 : ProxyCommand { protected override int Slot => 26; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd27 : ProxyCommand { protected override int Slot => 27; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd28 : ProxyCommand { protected override int Slot => 28; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd29 : ProxyCommand { protected override int Slot => 29; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd30 : ProxyCommand { protected override int Slot => 30; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd31 : ProxyCommand { protected override int Slot => 31; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd32 : ProxyCommand { protected override int Slot => 32; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd33 : ProxyCommand { protected override int Slot => 33; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd34 : ProxyCommand { protected override int Slot => 34; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd35 : ProxyCommand { protected override int Slot => 35; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd36 : ProxyCommand { protected override int Slot => 36; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd37 : ProxyCommand { protected override int Slot => 37; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd38 : ProxyCommand { protected override int Slot => 38; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd39 : ProxyCommand { protected override int Slot => 39; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd40 : ProxyCommand { protected override int Slot => 40; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd41 : ProxyCommand { protected override int Slot => 41; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd42 : ProxyCommand { protected override int Slot => 42; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd43 : ProxyCommand { protected override int Slot => 43; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd44 : ProxyCommand { protected override int Slot => 44; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd45 : ProxyCommand { protected override int Slot => 45; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd46 : ProxyCommand { protected override int Slot => 46; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd47 : ProxyCommand { protected override int Slot => 47; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd48 : ProxyCommand { protected override int Slot => 48; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd49 : ProxyCommand { protected override int Slot => 49; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd50 : ProxyCommand { protected override int Slot => 50; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd51 : ProxyCommand { protected override int Slot => 51; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd52 : ProxyCommand { protected override int Slot => 52; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd53 : ProxyCommand { protected override int Slot => 53; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd54 : ProxyCommand { protected override int Slot => 54; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd55 : ProxyCommand { protected override int Slot => 55; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd56 : ProxyCommand { protected override int Slot => 56; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd57 : ProxyCommand { protected override int Slot => 57; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd58 : ProxyCommand { protected override int Slot => 58; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd59 : ProxyCommand { protected override int Slot => 59; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd60 : ProxyCommand { protected override int Slot => 60; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd61 : ProxyCommand { protected override int Slot => 61; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd62 : ProxyCommand { protected override int Slot => 62; }
    [Transaction(TransactionMode.Manual)] public sealed class Cmd63 : ProxyCommand { protected override int Slot => 63; }
}
