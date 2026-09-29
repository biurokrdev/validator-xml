namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>
/// Stabilne kody ustaleń diagnostyki „Kondycja dokumentu". Kody etapu Pakiet OPC pochodzą
/// z <see cref="StructureInspection.StructureIssueCodes"/> (ten sam analizator), pozostałe są własne.
/// </summary>
public static class DocumentHealthCodes
{
    // ── Plik / kontener ───────────────────────────────────────────────────────
    public const string FileEmpty = "FILE_EMPTY";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string FileNotOoxmlPackage = "FILE_NOT_OOXML_PACKAGE";
    public const string FileEncryptedPackage = "FILE_ENCRYPTED_PACKAGE";
    public const string FileLegacyBinaryDoc = "FILE_LEGACY_BINARY_DOC";
    public const string FileZipUnreadable = "FILE_ZIP_UNREADABLE";
    public const string FileZipLeadingData = "FILE_ZIP_LEADING_DATA";
    public const string FileZipTrailingData = "FILE_ZIP_TRAILING_DATA";
    public const string FileZipEmpty = "FILE_ZIP_EMPTY";
    public const string FileNotWordPackage = "FILE_NOT_WORD_PACKAGE";
    public const string ZipEntryUnreadable = "ZIP_ENTRY_UNREADABLE";
    public const string ZipEntryCrcMismatch = "ZIP_ENTRY_CRC_MISMATCH";
    public const string ZipEntrySizeMismatch = "ZIP_ENTRY_SIZE_MISMATCH";
    public const string ZipEntryDuplicate = "ZIP_ENTRY_DUPLICATE";
    public const string ZipEntryUnsafePath = "ZIP_ENTRY_UNSAFE_PATH";
    public const string ZipEntryTooLarge = "ZIP_ENTRY_TOO_LARGE";
    public const string ZipTooManyEntries = "ZIP_TOO_MANY_ENTRIES";
    public const string ZipTotalSizeExceeded = "ZIP_TOTAL_SIZE_EXCEEDED";
    public const string ZipCompressionRatioSuspicious = "ZIP_COMPRESSION_RATIO_SUSPICIOUS";

    // ── Pakiet OPC (własne, uzupełniające analizator OPC) ─────────────────────
    public const string ContentTypesPartMissing = "OPC_CONTENT_TYPES_PART_MISSING";
    public const string RootRelationshipsPartMissing = "OPC_ROOT_RELATIONSHIPS_PART_MISSING";
    public const string PackageAnalysisFailed = "OPC_ANALYSIS_FAILED";
    public const string MacroEnabledDocument = "OPC_MACRO_ENABLED_DOCUMENT";
    public const string TemplateContentType = "OPC_TEMPLATE_CONTENT_TYPE";
    public const string MacrosPresent = "OPC_MACROS_PRESENT";
    public const string SignedPackage = "OPC_SIGNED_PACKAGE";
    public const string OptionalPartMissing = "OPC_OPTIONAL_PART_MISSING";

    // ── XML ───────────────────────────────────────────────────────────────────
    public const string XmlEmpty = "XML_PART_EMPTY";
    public const string XmlNotWellFormed = "XML_NOT_WELL_FORMED";
    public const string XmlDtdPresent = "XML_DTD_PRESENT";
    public const string XmlRootUnexpected = "XML_ROOT_UNEXPECTED";
    public const string XmlIgnorablePrefixUndeclared = "XML_MC_IGNORABLE_PREFIX_UNDECLARED";
    public const string XmlStrictNamespaces = "XML_STRICT_OOXML";

    // ── Struktura WordprocessingML ────────────────────────────────────────────
    public const string BodyMissing = "DOC_BODY_MISSING";
    public const string BodyEmpty = "DOC_BODY_EMPTY";
    public const string SectionPropertiesMisplaced = "DOC_SECTPR_MISPLACED";
    public const string SectionPropertiesMissing = "DOC_SECTPR_MISSING";
    public const string PageSizeOutOfRange = "DOC_PAGE_SIZE_OUT_OF_RANGE";
    public const string PageMarginsExceedPage = "DOC_PAGE_MARGINS_EXCEED_PAGE";
    public const string TableWithoutRows = "DOC_TABLE_WITHOUT_ROWS";
    public const string TableRowWithoutCells = "DOC_TABLE_ROW_WITHOUT_CELLS";
    public const string TableCellWithoutParagraph = "DOC_TABLE_CELL_WITHOUT_PARAGRAPH";
    public const string TablesAdjacent = "DOC_TABLES_ADJACENT";
    public const string TableNestingDeep = "DOC_TABLE_NESTING_DEEP";
    public const string TableGridMissing = "DOC_TABLE_GRID_MISSING";
    public const string ParagraphNested = "DOC_PARAGRAPH_NESTED";
    public const string RunOutsideParagraph = "DOC_RUN_OUTSIDE_PARAGRAPH";
    public const string TextOutsideRun = "DOC_TEXT_OUTSIDE_RUN";
    public const string FieldUnbalanced = "DOC_FIELD_UNBALANCED";
    public const string BookmarkUnclosed = "DOC_BOOKMARK_UNCLOSED";
    public const string CommentRangeUnbalanced = "DOC_COMMENT_RANGE_UNBALANCED";
    public const string DrawingExtentMissing = "DOC_DRAWING_EXTENT_MISSING";
    public const string DrawingExtentInvalid = "DOC_DRAWING_EXTENT_INVALID";
    public const string DrawingDocPrDuplicate = "DOC_DRAWING_DOCPR_DUPLICATE";
    public const string ImageRelationshipMissing = "DOC_IMAGE_RELATIONSHIP_MISSING";
    public const string ImageBlipWithoutSource = "DOC_IMAGE_BLIP_WITHOUT_SOURCE";
    public const string ImagePartEmpty = "DOC_IMAGE_PART_EMPTY";
    public const string ImageContentTypeMismatch = "DOC_IMAGE_CONTENT_TYPE_MISMATCH";
    public const string ImageUnrecognized = "DOC_IMAGE_UNRECOGNIZED";
    public const string ImageLarge = "DOC_IMAGE_LARGE";
    public const string ImagesTotalLarge = "DOC_IMAGES_TOTAL_LARGE";
    public const string ImageLinkedExternal = "DOC_IMAGE_LINKED_EXTERNAL";
    public const string HyperlinkRelationshipMissing = "DOC_HYPERLINK_RELATIONSHIP_MISSING";
    public const string AltChunkPresent = "DOC_ALT_CHUNK_PRESENT";
    public const string AltChunkRelationshipMissing = "DOC_ALT_CHUNK_RELATIONSHIP_MISSING";
    public const string EmbeddedObjectPresent = "DOC_EMBEDDED_OBJECT_PRESENT";
    public const string EmbeddedObjectRelationshipMissing = "DOC_EMBEDDED_OBJECT_RELATIONSHIP_MISSING";
    public const string LegacyVmlPresent = "DOC_LEGACY_VML_PRESENT";
    public const string AlternateContentWithoutFallback = "DOC_MC_FALLBACK_MISSING";
    public const string NoteReferenceTargetMissing = "DOC_NOTE_REFERENCE_TARGET_MISSING";
    public const string NoteIdDuplicate = "DOC_NOTE_ID_DUPLICATE";
    public const string NumberingInstanceMissing = "DOC_NUMBERING_INSTANCE_MISSING";
    public const string AbstractNumberingMissing = "DOC_ABSTRACT_NUMBERING_MISSING";
    public const string StylesPartMissing = "DOC_STYLES_PART_MISSING";
    public const string StyleMissing = "DOC_STYLE_MISSING";
    public const string ContentControlWithoutContent = "DOC_SDT_CONTENT_MISSING";
    public const string TrackedChangesPresent = "DOC_TRACKED_CHANGES_PRESENT";
    public const string CommentsPresent = "DOC_COMMENTS_PRESENT";
    public const string MailMergeSettings = "DOC_MAIL_MERGE_SETTINGS";
    public const string UpdateFieldsOnOpen = "DOC_UPDATE_FIELDS_ON_OPEN";
    public const string AttachedTemplate = "DOC_ATTACHED_TEMPLATE";
    public const string DocumentProtection = "DOC_DOCUMENT_PROTECTION";
    public const string EmbeddedFontMissing = "DOC_EMBEDDED_FONT_MISSING";
    public const string EmbeddedFontObfuscated = "DOC_EMBEDDED_FONT_OBFUSCATED";
    public const string DocumentVeryLarge = "DOC_VERY_LARGE";

    // ── Próby konwersji ───────────────────────────────────────────────────────
    public const string SdkOpenFailed = "PROBE_SDK_OPEN_FAILED";
    public const string SchemaErrors = "PROBE_SCHEMA_ERRORS";
    public const string UploadGateRejected = "PROBE_UPLOAD_GATE_REJECTED";
    public const string EditorImportFailed = "PROBE_EDITOR_IMPORT_FAILED";
    public const string PdfConversionFailed = "PROBE_PDF_CONVERSION_FAILED";
    public const string PdfOutputInvalid = "PROBE_PDF_OUTPUT_INVALID";
    public const string LibreOfficeFailed = "PROBE_LIBREOFFICE_FAILED";
    public const string RoundTripFailed = "PROBE_ROUND_TRIP_FAILED";
    public const string RoundTripFallback = "PROBE_ROUND_TRIP_FALLBACK";

    // ── Nasza implementacja (luki pipeline'u reader → edytor → writer) ───────
    public const string AppFeatureUnsupported = "APP_FEATURE_UNSUPPORTED";
    public const string AppFeaturePartial = "APP_FEATURE_PARTIAL";
    public const string AppFeaturePassThrough = "APP_FEATURE_PASS_THROUGH";
    public const string AppFeatureUnverified = "APP_FEATURE_UNVERIFIED";
    public const string AppRoundTripLoss = "APP_ROUND_TRIP_LOSS";
    public const string AppRoundTripIntroducedIssue = "APP_ROUND_TRIP_INTRODUCED_ISSUE";
    public const string AppRoundTripSchemaErrors = "APP_ROUND_TRIP_SCHEMA_ERRORS";

    // ── Narzędzie ─────────────────────────────────────────────────────────────
    public const string StageFailed = "TOOL_STAGE_FAILED";
}
