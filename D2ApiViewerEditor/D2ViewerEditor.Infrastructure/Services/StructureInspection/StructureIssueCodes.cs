namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public static class StructureIssueCodes
{
    public const string ContentTypesRootInvalid = "OPC_CONTENT_TYPES_ROOT_INVALID";
    public const string ContentTypeDefaultInvalid = "OPC_CONTENT_TYPE_DEFAULT_INVALID";
    public const string ContentTypeDefaultDuplicate = "OPC_CONTENT_TYPE_DEFAULT_DUPLICATE";
    public const string ContentTypeOverrideInvalid = "OPC_CONTENT_TYPE_OVERRIDE_INVALID";
    public const string ContentTypeOverrideDuplicate = "OPC_CONTENT_TYPE_OVERRIDE_DUPLICATE";
    public const string ContentTypeOverrideTargetNotFound = "OPC_CONTENT_TYPE_OVERRIDE_TARGET_NOT_FOUND";
    public const string ContentTypeMissing = "OPC_CONTENT_TYPE_MISSING";
    public const string RelationshipsXmlInvalid = "OPC_RELATIONSHIPS_XML_INVALID";
    public const string RelationshipsRootInvalid = "OPC_RELATIONSHIPS_ROOT_INVALID";
    public const string RelationshipSourceNotFound = "OPC_RELATIONSHIP_SOURCE_NOT_FOUND";
    public const string RelationshipInvalid = "OPC_RELATIONSHIP_INVALID";
    public const string RelationshipIdDuplicate = "OPC_RELATIONSHIP_ID_DUPLICATE";
    public const string RelationshipTargetModeInvalid = "OPC_RELATIONSHIP_TARGET_MODE_INVALID";
    public const string RelationshipTargetEscapesPackage = "OPC_RELATIONSHIP_TARGET_ESCAPES_PACKAGE";
    public const string RelationshipTargetMissing = "OPC_RELATIONSHIP_TARGET_NOT_FOUND";
    public const string RelationshipExternal = "OPC_EXTERNAL_RELATIONSHIP";
    public const string OrphanedPart = "OPC_ORPHANED_PART";
    public const string MainDocumentRelationshipMissing = "OPC_MAIN_DOCUMENT_RELATIONSHIP_MISSING";
    public const string MultipleMainDocumentRelationships = "OPC_MULTIPLE_MAIN_DOCUMENT_RELATIONSHIPS";
    public const string MainDocumentContentTypeInvalid = "OPC_MAIN_DOCUMENT_CONTENT_TYPE_INVALID";
    public const string MainDocumentFallback = "OPC_MAIN_DOCUMENT_FALLBACK";
    public const string StrictOoxml = "STRICT_OOXML";

    public const string ElementRelationshipNotDeclared = "RELATIONSHIP_NOT_DECLARED";
    public const string ElementRelationshipTargetMissing = "RELATIONSHIP_TARGET_NOT_FOUND";
    public const string ElementRelationshipExternal = "RELATIONSHIP_EXTERNAL";

    public const string DrawingAnchored = "DRAWING_ANCHORED";
    public const string DrawingInline = "DRAWING_INLINE";
    public const string DrawingBehindDocument = "DRAWING_BEHIND_DOCUMENT";
    public const string DrawingOverlapAllowed = "DRAWING_OVERLAP_ALLOWED";
    public const string DrawingOutsideCellLayout = "DRAWING_OUTSIDE_CELL_LAYOUT";
    public const string DrawingSimplePosition = "DRAWING_SIMPLE_POSITION";
    public const string DrawingWrapMissing = "DRAWING_WRAP_MISSING";
    public const string DrawingComplexWrap = "DRAWING_COMPLEX_WRAP";
    public const string DrawingNegativeOffset = "DRAWING_NEGATIVE_OFFSET";
    public const string DrawingZeroExtent = "DRAWING_ZERO_EXTENT";
    public const string DrawingHugeExtent = "DRAWING_HUGE_EXTENT";
    public const string DrawingEffectExtent = "DRAWING_EFFECT_EXTENT";
    public const string DrawingRelativeSize = "DRAWING_RELATIVE_SIZE";
    public const string DrawingTransform = "DRAWING_TRANSFORM";
    public const string DrawingGroupedShape = "DRAWING_GROUPED_SHAPE";
    public const string DrawingSvg = "DRAWING_SVG";
    public const string DrawingChart = "DRAWING_CHART";
    public const string DrawingSmartArt = "DRAWING_SMARTART";
    public const string ImageCropped = "IMAGE_CROPPED";
    public const string ImageLinked = "IMAGE_LINKED";
    public const string TextBoxContent = "TEXTBOX_CONTENT";
    public const string EmbeddedObject = "EMBEDDED_OBJECT";
    public const string LegacyVmlShape = "LEGACY_VML_SHAPE";
    public const string LegacyPictureContainer = "LEGACY_PICTURE_CONTAINER";

    public const string AlternateContent = "MC_ALTERNATE_CONTENT";
    public const string AlternateContentNoFallback = "MC_FALLBACK_MISSING";
    public const string ChoiceRequiresMissing = "MC_CHOICE_REQUIRES_MISSING";
    public const string CompatibilityPrefixUnresolved = "MC_PREFIX_UNRESOLVED";
    public const string UnknownNamespace = "UNKNOWN_NAMESPACE";

    public const string DirectFormattingPresent = "DIRECT_FORMATTING_PRESENT";
    public const string RedundantDirectFormatting = "REDUNDANT_DIRECT_FORMATTING";
    public const string StylesPartNotFound = "STYLES_PART_NOT_FOUND";
    public const string StyleNotFound = "STYLE_NOT_FOUND";
    public const string StyleBasedOnNotFound = "STYLE_BASED_ON_NOT_FOUND";
    public const string StyleInheritanceCycle = "STYLE_INHERITANCE_CYCLE";
    public const string TableStyleConditionalFormatting = "TABLE_STYLE_CONDITIONAL_FORMATTING";
    public const string HiddenText = "HIDDEN_TEXT";
    public const string CharacterScaling = "CHARACTER_SCALING";
    public const string CharacterSpacing = "CHARACTER_SPACING";
    public const string BidirectionalText = "BIDIRECTIONAL_TEXT";
    public const string NegativeIndentation = "NEGATIVE_INDENTATION";
    public const string ExactLineSpacing = "EXACT_LINE_SPACING";
    public const string CustomTabStops = "CUSTOM_TAB_STOPS";

    public const string NumberingPartNotFound = "NUMBERING_PART_NOT_FOUND";
    public const string NumberingInstanceNotFound = "NUMBERING_INSTANCE_NOT_FOUND";
    public const string AbstractNumberingNotFound = "ABSTRACT_NUMBERING_NOT_FOUND";
    public const string NumberingLevelNotFound = "NUMBERING_LEVEL_NOT_FOUND";
    public const string NumberingLevelOverride = "NUMBERING_LEVEL_OVERRIDE";

    public const string TableFloating = "TABLE_FLOATING";
    public const string TableNested = "TABLE_NESTED";
    public const string TableGridMissing = "TABLE_GRID_MISSING";
    public const string TableGridMismatch = "TABLE_GRID_MISMATCH";
    public const string TableAutoWidth = "TABLE_AUTO_WIDTH";
    public const string TableVerticalMergeWithoutRestart = "TABLE_VMERGE_WITHOUT_RESTART";
    public const string TableCellDuplicateProperties = "TABLE_CELL_DUPLICATE_PROPERTIES";
    public const string TableCellHorizontalMerge = "TABLE_CELL_HORIZONTAL_MERGE";
    public const string TableRowGridOffset = "TABLE_ROW_GRID_OFFSET";

    public const string SectionMultiColumn = "SECTION_MULTICOLUMN";
    public const string SectionNegativeMargin = "SECTION_NEGATIVE_MARGIN";
    public const string SectionPageBorders = "SECTION_PAGE_BORDERS";
    public const string HeaderFooterReferenceDuplicate = "HEADER_FOOTER_REFERENCE_DUPLICATE";
    public const string HeaderFooterRelationshipIdMissing = "HEADER_FOOTER_RELATIONSHIP_ID_MISSING";
    public const string HeaderFooterRelationshipNotFound = "HEADER_FOOTER_RELATIONSHIP_NOT_FOUND";
    public const string HeaderFooterRelationshipTypeInvalid = "HEADER_FOOTER_RELATIONSHIP_TYPE_INVALID";
    public const string HeaderFooterPartNotFound = "HEADER_FOOTER_PART_NOT_FOUND";
    public const string HeaderFooterExternalRelationship = "HEADER_FOOTER_EXTERNAL_RELATIONSHIP";
    public const string HeaderFooterPartOrphaned = "HEADER_FOOTER_PART_ORPHANED";

    public const string FieldSeparatorWithoutBegin = "FIELD_SEPARATOR_WITHOUT_BEGIN";
    public const string FieldEndWithoutBegin = "FIELD_END_WITHOUT_BEGIN";
    public const string FieldNotClosed = "FIELD_NOT_CLOSED";
    public const string FieldWithoutSeparator = "FIELD_WITHOUT_SEPARATOR";
    public const string FieldInstructionEmpty = "FIELD_INSTRUCTION_EMPTY";
    public const string FieldInstructionOutsideField = "FIELD_INSTRUCTION_OUTSIDE_FIELD";
    public const string FieldUncommonType = "FIELD_UNCOMMON_TYPE";
    public const string FieldNested = "FIELD_NESTED";

    public const string ReferenceIdMissing = "REFERENCE_ID_MISSING";
    public const string ReferenceTargetNotFound = "REFERENCE_TARGET_NOT_FOUND";
    public const string ReferenceIdDuplicate = "REFERENCE_ID_DUPLICATE";

    public const string ContentControlPropertiesMissing = "SDT_PROPERTIES_MISSING";
    public const string ContentControlDataBinding = "SDT_DATA_BINDING";
    public const string ContentControlStoreItemIdMissing = "SDT_STORE_ITEM_ID_MISSING";
    public const string ContentControlCustomXmlItemNotFound = "SDT_CUSTOM_XML_ITEM_NOT_FOUND";
    public const string ContentControlXPathMissing = "SDT_XPATH_MISSING";
    public const string ContentControlXPathInvalid = "SDT_XPATH_INVALID";
    public const string ContentControlXPathNoMatch = "SDT_XPATH_NO_ELEMENT_MATCH";
    public const string ContentControlPlaceholder = "SDT_PLACEHOLDER";

    public const string TrackedRevision = "TRACKED_REVISION";
    public const string RevisionRangeEndMissing = "REVISION_RANGE_END_MISSING";
    public const string RevisionRangeStartMissing = "REVISION_RANGE_START_MISSING";

    public const string EditorFeatureUnsupported = "EDITOR_FEATURE_UNSUPPORTED";
    public const string EditorFeaturePartial = "EDITOR_FEATURE_PARTIAL";

    public const string AnalyzerFailed = "ANALYZER_FAILED";

    public const string SchemaValidationPrefix = "SCHEMA_";
    public const string SchemaValidationFailed = "OPENXML_SDK_VALIDATION_FAILED";
}
