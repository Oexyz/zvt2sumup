using System.Collections.ObjectModel;

namespace Zvt2SumUp.Protocol;

public sealed record ZvtCommandInfo(byte Class, byte Instruction, string Name, string Direction = "ECR->PT", string Status = "known")
{
    public ushort Id => (ushort)((Class << 8) | Instruction);
}
public sealed record ZvtCommand(byte Class, byte Instruction, ReadOnlyMemory<byte> Data)
{
    public ushort Id => (ushort)((Class << 8) | Instruction);
    public ZvtCommandInfo? Info => ZvtCommandRegistry.TryGet(Class, Instruction, out ZvtCommandInfo? value) ? value : null;
    public string Name => Info?.Name ?? $"Unbekannt ({Class:X2} {Instruction:X2})";
    public bool IsAcknowledgement => Class is 0x80 or 0x84;
}

public static class ZvtCommandIds
{
    public const ushort StatusEnquiry = 0x0501;
    public const ushort Registration = 0x0600;
    public const ushort Authorization = 0x0601;
    public const ushort LogOff = 0x0602;
    public const ushort Completion = 0x060F;
    public const ushort TurnoverTotals = 0x0610;
    public const ushort PrintTurnoverReceipts = 0x0612;
    public const ushort Reset = 0x0618;
    public const ushort AbortResponse = 0x061E;
    public const ushort RepeatReceipt = 0x0620;
    public const ushort Reversal = 0x0630;
    public const ushort Refund = 0x0631;
    public const ushort Reconciliation = 0x0650;
    public const ushort PartialReconciliation = 0x0652;
    public const ushort Diagnosis = 0x0670;
    public const ushort SelfTest = 0x0679;
    public const ushort SetDateTime = 0x0691;
    public const ushort Initialization = 0x0693;
    public const ushort Abort = 0x06B0;
}

public static class ZvtResultCode
{
    public const byte Ok = 0x00;
    public const byte CardNotReadable = 0x64;
    public const byte ProcessingError = 0x66;
    public const byte AbortTimeoutOrKey = 0x6C;
    public const byte WrongCurrency = 0x6F;
    public const byte FunctionNotPossible = 0x83;
    public const byte ProtocolError = 0x9A;
    public const byte NoConnection = 0xA3;
    public const byte AlreadyReversed = 0xB4;
    public const byte ReversalNotPossible = 0xB5;
    public const byte AmountTooSmall = 0xC8;
    public const byte SystemError = 0xFF;
}

public static class ZvtStatusCode
{
    public const byte Ready = 0x00;
    public const byte OutOfOrder = 0xDF;
}

public static class ZvtCommandRegistry
{
    private static readonly ReadOnlyDictionary<ushort, ZvtCommandInfo> Commands = new(
        new[]
        {
            C(0x01,0x01,"RFU", status:"rfu"), C(0x04,0x01,"Set Date and Time in ECR","PT->ECR"),
            C(0x04,0x0D,"Input-Request","PT->ECR"), C(0x04,0x0E,"Menu-Request","PT->ECR"),
            C(0x04,0x0F,"Status Information","PT->ECR"), C(0x04,0xFF,"Intermediate Status Information","PT->ECR"),
            C(0x05,0x01,"Status-Enquiry"), C(0x05,0xFF,"RFU",status:"rfu"),
            C(0x06,0x00,"Registrierung"), C(0x06,0x01,"Autorisierung (Zahlung)"), C(0x06,0x02,"Abmeldung"),
            C(0x06,0x03,"Account Balance Request"), C(0x06,0x04,"Activate Card"), C(0x06,0x05,"Procurement"),
            C(0x06,0x09,"Top-Up Prepaid-Cards"), C(0x06,0x0A,"Tax Free"), C(0x06,0x0B,"RFU",status:"rfu"),
            C(0x06,0x0C,"Book Tip"), C(0x06,0x0F,"Completion","PT->ECR"), C(0x06,0x10,"Send Turnover Totals"),
            C(0x06,0x11,"RFU",status:"rfu"), C(0x06,0x12,"Print Turnover Receipts"), C(0x06,0x18,"Reset Terminal"),
            C(0x06,0x1A,"Print System Configuration"), C(0x06,0x1B,"Set/Reset Terminal-ID"), C(0x06,0x1E,"Abort","PT->ECR"),
            C(0x06,0x20,"Repeat Receipt"), C(0x06,0x21,"Telephonic Authorisation"), C(0x06,0x22,"Pre-Authorisation / Reservation"),
            C(0x06,0x23,"Partial-Reversal / Booking of Reservation"), C(0x06,0x24,"Book Total"),
            C(0x06,0x25,"Pre-Authorisation Reversal"), C(0x06,0x26,"Reversal of external transaction"),
            C(0x06,0x30,"Storno"), C(0x06,0x31,"Refund"), C(0x06,0x50,"Kassenschnitt"),
            C(0x06,0x51,"Send offline Transactions"), C(0x06,0x52,"Partial reconciliation"),
            C(0x06,0x70,"Diagnose"), C(0x06,0x79,"Selftest"), C(0x06,0x82,"RFU",status:"rfu"),
            C(0x06,0x85,"Display Text (old version)"), C(0x06,0x86,"Display Text with Numerical Input (old version)"),
            C(0x06,0x87,"PIN-Verification for Customer-Card (old version)"), C(0x06,0x88,"Display Text with Function-Key Input (old version)"),
            C(0x06,0x90,"RFU",status:"rfu"), C(0x06,0x91,"Set Date and Time in PT"), C(0x06,0x93,"Initialisation"),
            C(0x06,0x95,"Change Password"), C(0x06,0xB0,"Abbruch"), C(0x06,0xC0,"Read Card"),
            C(0x06,0xC1,"reserved",status:"reserved"), C(0x06,0xC2,"reserved",status:"reserved"),
            C(0x06,0xC3,"reserved",status:"reserved"), C(0x06,0xC4,"reserved",status:"reserved"),
            C(0x06,0xC5,"Close Card Session"), C(0x06,0xC6,"Send APDUs"), C(0x06,0xCE,"RFU",status:"rfu"),
            C(0x06,0xD0,"Menu selection with graphic display"), C(0x06,0xD1,"Print Line"), C(0x06,0xD3,"Print Text-Block"),
            C(0x06,0xD4,"RFU",status:"rfu"), C(0x06,0xD8,"Dial-Up","PT->ECR"), C(0x06,0xD9,"Transmit Data via Dial-Up","PT->ECR"),
            C(0x06,0xDA,"Receive Data via Dial-Up","PT->ECR"), C(0x06,0xDB,"Hang-Up","PT->ECR"),
            C(0x06,0xDD,"Transparent-Mode","PT->ECR"), C(0x06,0xE0,"Display Text"),
            C(0x06,0xE1,"Display Text with Function-Key Input"), C(0x06,0xE2,"Display Text with Numerical Input"),
            C(0x06,0xE3,"PIN-Verification for Customer-Card"), C(0x06,0xE4,"Blocked-List Query to ECR","PT->ECR"),
            C(0x06,0xE5,"MAC calculation"), C(0x06,0xE6,"Card Poll with Authorization"),
            C(0x06,0xE7,"Display Text with Numerical Input with DUKPT Encryption"), C(0x06,0xF0,"Display Image"),
            C(0x06,0xF1,"Display Image with Function-Key Input"), C(0x08,0x01,"Activate Service-Mode"),
            C(0x08,0x02,"Switch Protocol"), C(0x08,0x03,"Configure Power Management"), C(0x08,0x10,"Software-Update"),
            C(0x08,0x11,"Read File"), C(0x08,0x12,"Delete File"), C(0x08,0x13,"Change Configuration"),
            C(0x08,0x14,"Write File"), C(0x08,0x20,"Start OPT Action"), C(0x08,0x21,"Set OPT Point-in-Time"),
            C(0x08,0x22,"Start OPT Pre-Initialisation"), C(0x08,0x23,"Output OPT-Data"), C(0x08,0x24,"OPT Out-of-Order"),
            C(0x08,0x30,"Select Language"), C(0x08,0x40,"Change Baudrate"), C(0x08,0x50,"Activate Card-Reader"),
            C(0x0F,0xCA,"ChipActivator",status:"proprietary"), C(0x80,0x00,"Positive acknowledgement","ECR<->PT","ack"),
            C(0x84,0x00,"Positive acknowledgement","ECR<->PT","ack"), C(0x84,0x9C,"Repeat Status Information","ECR<->PT","ack")
        }.ToDictionary(x => x.Id));

    public static IReadOnlyDictionary<ushort, ZvtCommandInfo> All => Commands;
    public static bool TryGet(byte commandClass, byte instruction, out ZvtCommandInfo? info) =>
        Commands.TryGetValue((ushort)((commandClass << 8) | instruction), out info);

    private static ZvtCommandInfo C(byte c, byte i, string name, string direction = "ECR->PT", string status = "known") =>
        new(c, i, name, direction, status);
}
