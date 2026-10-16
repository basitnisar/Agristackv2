// JamabandiModels.cs
// ------------------------------------------------------------------------------------
// Domain models + view models for the J&K Jamabandi (village register of land rights),
// as rendered by the Agristack portal and studied for the Agristack Owner-Area Filler.
//
// Target: .NET 8 / C# 12, nullable enabled. No dependencies.
//
// Everything here was calibrated against real registers (Ratsoona and Tokoonah of
// district Pulwama, plus reference registers). The XML comments carry the field rules
// learned from them - read them; they are the domain, not decoration.
//
// The single most important modelling decision, repeated throughout:
//   NUMBERS FROM THE REGISTER ARE STRINGS. A khewat's old number can be "48/1"
//   (an entry inserted after the serial was fixed), a khata's old number can be
//   "828,820" (two merged khatas, with a comma), a khasra number can be a lineage
//   chain "2171/1622/247". A century of clerks wrote whatever they needed. Parse
//   them for arithmetic where needed, but never store them as int.
// ------------------------------------------------------------------------------------

namespace Agristack.Jamabandi.Models;

#region Area arithmetic

/// <summary>
/// An area in the J&K land units: 1 kanal = 20 marla, 1 marla = 272.25 sq ft.
/// <para>
/// Because 272.25 x 4 = 1,089 is an INTEGER, the safe internal unit is the
/// quarter-square-foot (qsf): all sums in qsf are exact integer arithmetic with no
/// floating-point drift, and any qsf value converts back to a Sqft figure with at
/// most two decimals, which the portal's 4-decimal Sqft field stores losslessly.
/// Do all totalling and apportionment in qsf (<see cref="TotalQuarterSquareFeet"/>).
/// </para>
/// <para>
/// Note: the Jamabandi table itself carries Kanal and Marla columns only (no Sqft);
/// the ownership strings and the khasra screen carry Sqft too. SquareFeet is decimal
/// because real records hold values like 271.75 or 136.25.
/// </para>
/// </summary>
public readonly record struct LandArea(int Kanal, int Marla, decimal SquareFeet)
{
    public const decimal SquareFeetPerMarla = 272.25m;
    public const int MarlaPerKanal = 20;
    public const long QsfPerMarla = 1_089;   // 272.25 * 4 - an integer. The whole trick.
    public const long QsfPerKanal = 21_780;

    /// <summary>The exact total in quarter-square-feet. Use this for every sum and comparison.</summary>
    public long TotalQuarterSquareFeet =>
        Kanal * QsfPerKanal + Marla * QsfPerMarla + (long)decimal.Round(SquareFeet * 4m);

    public decimal TotalSquareFeet => TotalQuarterSquareFeet / 4m;

    /// <summary>Rebuild a normalised K/M/Sqft from a qsf count (Sqft comes out as a multiple of 0.25).</summary>
    public static LandArea FromQuarterSquareFeet(long qsf)
    {
        if (qsf < 0) qsf = 0;
        var kanal = (int)(qsf / QsfPerKanal); qsf -= kanal * QsfPerKanal;
        var marla = (int)(qsf / QsfPerMarla); qsf -= marla * QsfPerMarla;
        return new LandArea(kanal, marla, qsf / 4m);
    }

    /// <summary>Equality to the quarter-square-foot - the only meaningful equality for areas here.</summary>
    public bool SameAs(LandArea other) => TotalQuarterSquareFeet == other.TotalQuarterSquareFeet;

    public override string ToString() => $"{Kanal}K {Marla}M {SquareFeet}Sqft";
}

/// <summary>
/// An exact share written as a fraction, e.g. 14/24. Never convert to double for
/// comparisons: with many owners the common denominator overflows double precision
/// and 0.9999999999 passes as a whole. Sum fractions exactly (cross-multiply in
/// long/BigInteger) when checking that shares describe the whole holding.
/// </summary>
public readonly record struct Fraction(long Numerator, long Denominator)
{
    public override string ToString() => $"{Numerator}/{Denominator}";
}

#endregion

#region Register numbering

/// <summary>
/// A khewat or khata number: the current serial plus the superseded one the record keeps.
/// <para>
/// IMPORTANT - a slash here does NOT mean lineage (unlike khasra numbers). Per the
/// Patwari: when an account was missed after the serial list was fixed, it was squeezed
/// in as e.g. "48/1" ("the entry after 48") purely for numbering, and received a proper
/// serial at the next Jamabandi - which is why Ratsoona's khewat 41 carries old number
/// "48/1". Old numbers can also be merges: Tokoonah khata 1256 has old number "828,820"
/// (two old khatas merged, written with a comma). Hence: strings, always.
/// </para>
/// </summary>
public sealed record RecordNumber
{
    /// <summary>Current serial as printed, e.g. "41". Usually numeric; do not bet on it.</summary>
    public required string Current { get; init; }

    /// <summary>Old / superseded number as printed: "48/1", "828,820", "183", or null when none shown.</summary>
    public string? Old { get; init; }

    public override string ToString() => Old is null ? Current : $"{Current} (was {Old})";
}

/// <summary>
/// A khasra (plot) number. Here a slash DOES mean lineage, newest first: when khasra 5
/// of a 200-khasra village divides, the pieces take the next free serials in the village
/// series and become "201/5" and "202/5"; if 201 later divides, its piece is "204/201/5".
/// Real two-generation example from Ratsoona: "2171/1622/247" and "2175/1622/247" - both
/// cut from 1622, which was itself cut from 247. One khasra = one plot = one area,
/// however long its chain.
/// </summary>
public sealed record KhasraNumber
{
    /// <summary>The full number exactly as printed, e.g. "4839/470" or "2171/1622/247".</summary>
    public required string Full { get; init; }

    /// <summary>The chain split on '/': [newest, parent, ..., original]. Lineage[0] is the current number.</summary>
    public IReadOnlyList<string> Lineage { get; init; } = [];

    public string Current => Lineage.Count > 0 ? Lineage[0] : Full;

    public override string ToString() => Full;
}

#endregion

#region The owner column (ownership string)

/// <summary>How an owner relates to the person named in their description.</summary>
public enum RelationKind
{
    None = 0,
    /// <summary>s/o, pisar, pisaran (plural), bin, ibn, walad.</summary>
    SonOf,
    /// <summary>d/o, dukhtar, dukhtaran (plural), bint.</summary>
    DaughterOf,
    /// <summary>w/o, zoja, byuh, bewah (widow).</summary>
    WifeOrWidowOf,
    /// <summary>baradar (brother), nawasah (grandson), waris (heir), c/o...</summary>
    Other,
}

/// <summary>
/// The three kinds of chunk an ownership string contains. The tell between
/// HeirOfNamedPerson and ShareOfHolding is the WHOLE string, never one chunk:
/// "suhail ahmad s/o nazir ahmad bhat sakindeh 14/24" (an heir - khewat 12, where other
/// owners carry amounts) and "bashir ahmad s/o gaffar bhat sakindeh 14/32" (a plain
/// share - khewat 34, where nobody carries an amount) are the same shape letter for
/// letter. A fraction alongside somebody's amount divides that somebody; fractions
/// alone are the holding written in parts.
/// </summary>
public enum OwnerEntryKind
{
    /// <summary>Name + K/M/Sqft amount (+ tail). The amount is the KHEWAT holding, never a khasra share.</summary>
    Amount,
    /// <summary>Name + relation to a deceased person + tail + a fraction OF THAT PERSON'S holding.</summary>
    HeirOfNamedPerson,
    /// <summary>A fraction of the whole khewat, used as it stands (no amount anywhere in the string).</summary>
    ShareOfHolding,
}

/// <summary>Warnings a string can raise about itself - patterns that must not be parsed on faith.</summary>
public enum OwnershipStringWarning
{
    /// <summary>"vagerah" - "and others": the owner list is deliberately incomplete.</summary>
    AndOthers,
    /// <summary>"mandarja khata N" - the real owner list lives in another khata.</summary>
    ListedInOtherKhata,
    /// <summary>"mazkoor" - "the aforesaid", refers back to a group named earlier.</summary>
    Aforesaid,
    /// <summary>A fraction written as a word: nisf (1/2), sulus (1/3), rub (1/4).</summary>
    WordFraction,
    /// <summary>"N hissa" unit shares - resolve only against the whole holding's unit count.</summary>
    UnitShares,
}

/// <summary>
/// One owner as read out of the ownership string ("Existing Owner Details").
/// <para>
/// The register writes identity once and expects the reader to distribute it:
/// a trailing caste/residence phrase reaches BACKWARDS over preceding owners who have
/// none of their own (backfill), and a PLURAL relation word (pisaran = "sons of")
/// describes every name in the run before it, stopping at the first owner with a
/// description of their own. Khewat 6 of Ratsoona: "pisaran jila" covers six men.
/// Fields distributed that way should set <see cref="IsInferred"/> so the UI can show
/// that the fact was shared, not written against this owner.
/// </para>
/// </summary>
public sealed record OwnerEntry
{
    public required OwnerEntryKind Kind { get; init; }

    /// <summary>Owner's name, with any woman's title (mst / musammat) already stripped - it is a title, not a name.</summary>
    public required string Name { get; init; }

    public RelationKind Relation { get; init; }

    /// <summary>
    /// The person named in the relation: a father, a husband - or, for an heir entry,
    /// the deceased whose holding the fraction divides. May legitimately be null or
    /// empty: real strings contain "pisar" with no name after it ("ahad ... pisar ,"
    /// on khewat 6). Record the absence; never invent a father.
    /// </summary>
    public string? RelatedPersonName { get; init; }

    public string? Caste { get; init; }

    /// <summary>Residence, from the first sakin* word onward (sakindeh / sakinandeh = resident of this village).</summary>
    public string? Address { get; init; }

    /// <summary>Basis-of-holding words that ended the description: qabiz, maurausi, hissadar, alati...</summary>
    public string? StatusWords { get; init; }

    /// <summary>
    /// For Kind == Amount: the KHEWAT holding exactly as written. A component can be
    /// NEGATIVE: khewat 19 really reads "abdulghani 5 kanal -6 marla 204 marabba foot" -
    /// the portal subtracted a sale from one component without normalising, and the
    /// un-normalised figure IS his correct net holding. Set <see cref="HasNegativeComponent"/>.
    /// </summary>
    public LandArea? Holding { get; init; }

    public bool HasNegativeComponent { get; init; }

    /// <summary>
    /// For Kind == HeirOfNamedPerson: the fraction OF THE DECEASED'S OWN KHEWAT HOLDING
    /// (not of the khasra, not of the khewat total). Khewat 12: son 14/24, daughter 7/24,
    /// widow 3/24 of nazir ahmad bhat - heir fractions sum to exactly 1.
    /// For Kind == ShareOfHolding: the fraction of the whole khewat, used as it stands.
    /// </summary>
    public Fraction? Share { get; init; }

    /// <summary>True when relation/caste/address were distributed onto this owner by backfill or plural reach-back.</summary>
    public bool IsInferred { get; init; }

    /// <summary>The owner's chunk verbatim. Keep it: the whole engine is spelling-sensitive, and audits need the source.</summary>
    public required string RawChunk { get; init; }
}

#endregion

#region The cultivator column (transfers)

/// <summary>
/// The transfer vocabulary. Every give-word maps to one of these; keep the raw word too
/// (<see cref="TransferHop.GiveWordRaw"/>) because districts spell freely and new
/// spellings are an expected, additive change.
/// </summary>
public enum TransferKind
{
    Unknown = 0,
    /// <summary>bayaan / bayan / baya.</summary>
    Sale,
    /// <summary>waahb / wahb / waban / wahiban...  (NB: "wahab" is a common FIRST NAME, not a give-word.)</summary>
    Gift,
    /// <summary>dagre + dhndah/dihindah.</summary>
    Decree,
    /// <summary>tabadilah + dhndah.</summary>
    Exchange,
    /// <summary>dastbradari - a man giving up his share parts with land like any other transfer.</summary>
    Relinquishment,
}

/// <summary>What one cultivator cell is, before any arithmetic.</summary>
public enum CultivatorCellKind
{
    Empty = 0,
    /// <summary>"khudkasht" - the owners till their own land. The commonest cell; no transfer.</summary>
    SelfCultivated,
    /// <summary>A cultivation list with no transfer word.</summary>
    Plain,
    /// <summary>Only a taker with a bracketed area - the seller's own owner line already carries the subtraction; owes nothing further.</summary>
    BuyerParcel,
    /// <summary>Givers + give-word(s): a deduction may be due (first hop only).</summary>
    Transfer,
    /// <summary>A transfer word with no giver before it - show it, deduct nothing.</summary>
    Unreadable,
}

/// <summary>One named party with a share inside a transfer, e.g. "mahda 1/1" or "gula 1/2".</summary>
public sealed record PartyShare
{
    public required string Name { get; init; }
    public required Fraction Share { get; init; }
}

/// <summary>
/// One transfer inside a cell: [givers] GIVE-WORD [takers] TAKE-WORD. A cell can chain
/// several hops ("A sells to B ... B passes to C" - khata 1265 of Tokoonah khewat 136 is
/// a real two-hop cell). RULE: only the FIRST hop is ever deducted for - later hops hand
/// on land that was never in the owner column. And if a hop's giver reappears among a
/// LATER hop's takers, the land came back to him: deduct nothing for that man.
/// </summary>
public sealed record TransferHop
{
    public required IReadOnlyList<PartyShare> Givers { get; init; }
    public required TransferKind Kind { get; init; }
    /// <summary>The give-word exactly as written ("bayaan", "waahb", "dastbradari"...).</summary>
    public required string GiveWordRaw { get; init; }
    public required IReadOnlyList<PartyShare> Takers { get; init; }
    /// <summary>The take-word as written: mushtari (buyer), mahub allya (receiver), girnida...</summary>
    public string? TakeWordRaw { get; init; }

    /// <summary>
    /// Shares counted in the giver block that could not be attached to a readable name.
    /// A non-zero count means a giver is KNOWN TO EXIST but unreadable - any deduction
    /// computed from this cell would be silently short. Stop and show the cell.
    /// </summary>
    public int UnnamedGiverCount { get; init; }
    public int UnnamedTakerCount { get; init; }
}

/// <summary>
/// The cultivator cell of ONE KHATA - and that placement is the arithmetic's foundation:
/// the cell is written once per khata and covers every khasra under it, so a giver's
/// fraction is a share of HIS STAKE IN THAT KHATA:
///   slice = hisKhewatHolding x (khataArea / sumOfAllKhataAreas);  gave = slice x n/d.
/// Summed over all khatas this returns exactly his holding, so giving 1/1 everywhere
/// empties a man and can never overdraw him. (The two rival readings - fraction of the
/// khasra, fraction of his whole holding - both produced impossible deductions on real
/// registers and were discarded.)
/// </summary>
public sealed record CultivatorCell
{
    public required CultivatorCellKind Kind { get; init; }

    /// <summary>The cell verbatim, Urdu script and page furniture already stripped.</summary>
    public required string RawText { get; init; }

    /// <summary>The transfers, in order. Empty unless Kind == Transfer.</summary>
    public IReadOnlyList<TransferHop> Hops { get; init; } = [];

    /// <summary>
    /// Everything after the word "kasht" ("tilled by"): tillers, who work the land and
    /// own nothing - no deduction can ever arise from them. ("khudkasht" must NOT trip
    /// this cut - match the word, not the letters.)
    /// </summary>
    public IReadOnlyList<PartyShare> Tillers { get; init; } = [];

    /// <summary>A transfer word found on the tiller side of the kasht cut - never act on it, always surface it.</summary>
    public string? TillerGiveWord { get; init; }

    /// <summary>For BuyerParcel: the bracketed area, e.g. "(1 kanal 2 marla 136 sqft)".</summary>
    public LandArea? BuyerBracketedArea { get; init; }

    /// <summary>A giver also appears among the takers of the SAME hop (khewat 209). Usually two men of one name told apart by parentage - deduct, but say so.</summary>
    public bool GiverAmongOwnTakers { get; init; }
}

#endregion

#region Mutations and deceased records

/// <summary>
/// A death recorded in the register. It appears two ways: a full Remarks entry naming
/// everything - "Inheritance Of Deceased: samad 43 kanal 15 marala 0 marabba foot;
/// Legal Heir: Ab Qayoom S/o Samad ... 2/7, ..." - or a bare "Warasat" mutation type
/// with EMPTY remarks, where the record names neither the dead man nor the heirs (then
/// only the mutation number, looked up in the mutation register, answers it).
/// One death = one event: the inheritance type repeats down every khata of the khewat
/// while the naming remark is written once - de-duplicate by mutation reference, or one
/// death asks seventeen questions.
/// </summary>
public sealed record DeceasedRecord
{
    public required string Name { get; init; }

    /// <summary>The estate as stated in the remark - the dead man's khewat holding. Null when the record is a bare Warasat.</summary>
    public LandArea? Holding { get; init; }

    public IReadOnlyList<HeirShare> Heirs { get; init; } = [];

    /// <summary>e.g. "Inheritance 125 jeem", "107jeem" - the key for de-duplication and for the mutation register.</summary>
    public string? MutationReference { get; init; }

    /// <summary>The khata the naming remark was found on.</summary>
    public string? Khata { get; init; }
}

/// <summary>One legal heir in a deceased remark, with the fraction of the deceased's holding.</summary>
public sealed record HeirShare
{
    public required string Name { get; init; }
    public RelationKind Relation { get; init; }
    public required Fraction Share { get; init; }
}

/// <summary>The Reference Mutation Type / Number pair a khata row can carry.</summary>
public sealed record MutationReference
{
    /// <summary>As printed: "Warasat", "Inheritance", "intiqal-e-warasat"... Match whole-cell only - "warasat" occurs inside names.</summary>
    public required string TypeRaw { get; init; }
    public required string Number { get; init; }
    public string? Remarks { get; init; }
    public bool IsInheritance { get; init; }
}

#endregion

#region The hierarchy: khasra, khata, khewat, jamabandi

/// <summary>One plot row of the register. The Jamabandi carries Kanal/Marla only (no Sqft column).</summary>
public sealed record KhasraRecord
{
    public required KhasraNumber Number { get; init; }

    /// <summary>Total area of the plot (the LAST soil figure in the row is the total; per-soil figures precede it).</summary>
    public required LandArea Area { get; init; }

    /// <summary>Soil classification (Qism Zameen) portions, e.g. ABI SOYIM 15K 16M, Gair Mumkin 0K 6M. They sum to Area.</summary>
    public IReadOnlyList<SoilPortion> Soils { get; init; } = [];
}

public sealed record SoilPortion
{
    /// <summary>As printed - spellings vary freely ("abe awal" / "Abi Awal" are the same soil romanised by different hands).</summary>
    public required string Name { get; init; }
    public required LandArea Area { get; init; }
}

/// <summary>
/// One cultivation account. CARDINAL FACT: the cultivator cell belongs HERE, once per
/// khata, spanning all its khasras (the portal draws it with grid-row span, so covered
/// rows simply omit the cell - never read that table by column position).
/// </summary>
public sealed record KhataRecord
{
    public required RecordNumber Number { get; init; }

    /// <summary>
    /// "Total Area For Khata" as printed. The record's own figures are not always whole
    /// ("348 Kanal 17.55 Marla" is real) - compare in qsf with a small tolerance, and
    /// treat a missing total as "size unknown, compute nothing", not as zero.
    /// </summary>
    public LandArea? TotalArea { get; init; }

    public required IReadOnlyList<KhasraRecord> Khasras { get; init; }

    public CultivatorCell? Cultivator { get; init; }

    public MutationReference? Mutation { get; init; }

    /// <summary>
    /// True for the unnumbered trailing parcels a khewat can end with (khewat 211 has
    /// four): created by mutation, not yet folded into the numbering - each with its own
    /// cultivator cell and total. Keep them, and keep them unnumbered.
    /// </summary>
    public bool IsUnnumbered { get; init; }
}

/// <summary>One ownership account: a group of owners and everything they hold together in the village.</summary>
public sealed record KhewatRecord
{
    public required RecordNumber Number { get; init; }

    /// <summary>The ownership passage ("Existing Owner Details") verbatim - the record of record. Keep it always.</summary>
    public required string OwnerStringRaw { get; init; }

    /// <summary>The string read into owners. Figures here are KHEWAT-level; converting to khasra shares is downstream arithmetic.</summary>
    public IReadOnlyList<OwnerEntry> Owners { get; init; } = [];

    public IReadOnlyList<OwnershipStringWarning> Warnings { get; init; } = [];

    /// <summary>"Total Area For Khewat" - the whole account. (Sum of khata totals should match within ~a quarter-sqft per khata.)</summary>
    public LandArea? TotalArea { get; init; }

    public required IReadOnlyList<KhataRecord> Khatas { get; init; }

    /// <summary>Deaths recorded anywhere in this khewat, de-duplicated by mutation reference.</summary>
    public IReadOnlyList<DeceasedRecord> Deceased { get; init; } = [];

    /// <summary>Portal workflow status as printed, e.g. "Approved".</summary>
    public string? Status { get; init; }
}

/// <summary>The register of one village - the root aggregate.</summary>
public sealed record JamabandiDocument
{
    public required string District { get; init; }
    public required string Tehsil { get; init; }
    public required string Village { get; init; }

    /// <summary>The settlement / revision period if known, e.g. "2015-16". As printed.</summary>
    public string? Period { get; init; }

    public required IReadOnlyList<KhewatRecord> Khewats { get; init; }
}

#endregion

#region View models

/// <summary>
/// One flat row of the on-screen Jamabandi table, mirroring the portal's columns.
/// Everything is a string because that is what the register is; the structured truth
/// lives in the domain records above. Rows covered by a spanning khata/cultivator cell
/// have those fields null - that is the register's own shape, preserve it.
/// </summary>
public sealed record JamabandiRowViewModel
{
    public string? KhewatNo { get; init; }
    public string? KhewatNoOld { get; init; }
    public string? KhataNo { get; init; }
    public string? KhataNoOld { get; init; }
    public string? KhasraNo { get; init; }
    public string? QismZameen { get; init; }
    public string? Kanal { get; init; }
    public string? Marla { get; init; }
    public string? Cultivator { get; init; }
    public string? Owner { get; init; }
    public string? ReferenceMutationType { get; init; }
    public string? MutationNumber { get; init; }
    public string? Remarks { get; init; }
}

/// <summary>An owner prepared for display, with the inferred facts marked as such.</summary>
public sealed record OwnerDisplayViewModel
{
    public required string Name { get; init; }
    public string? RelationLabel { get; init; }        // "S/o", "D/o", "W/o", "Others"
    public string? RelatedPersonName { get; init; }
    public string? Caste { get; init; }
    public string? Address { get; init; }
    public string? HoldingDisplay { get; init; }        // "0K 18M 53Sqft" - khewat level!
    public string? ShareDisplay { get; init; }          // "14/24 of nazir ahmad bhat" / "14/32 of the khewat"
    /// <summary>Show visibly when identity was distributed by backfill / plural reach-back - an inferred fact must never look written.</summary>
    public bool IsInferred { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A khewat summarised for lists and dashboards.</summary>
public sealed record KhewatSummaryViewModel
{
    public required string KhewatNo { get; init; }
    public string? KhewatNoOld { get; init; }
    public int OwnerCount { get; init; }
    public int KhataCount { get; init; }
    public int KhasraCount { get; init; }
    public string? TotalAreaDisplay { get; init; }
    public string? Status { get; init; }
    public bool HasTransfers { get; init; }
    public bool HasInheritance { get; init; }
    public bool HasWarnings { get; init; }
}

#endregion
