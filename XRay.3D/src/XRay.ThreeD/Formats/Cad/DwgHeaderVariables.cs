using System.Globalization;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// The header variables of a DWG (AC1015 and later): the ordered run of bit coded values that begins the header section, as the release writes it. The order
/// and the conditions per release follow the header reader of the MIT-licensed ACadSharp (see THIRD_PARTY_NOTICES.md), converted to this repository's bit reader;
/// values are kept under the property names that reader uses (<c>InsUnits</c>, <c>ModelSpaceExtMin</c>, <c>LineTypeScale</c>, ...), numbers as doubles (booleans as 0 or 1,
/// points as name + X, Y, Z), texts apart. From AC1021 on the handles of the header live in a separate stream and are not read.
/// </summary>
internal sealed class DwgHeaderVariables
{
    private readonly HeaderReader _reader;
    private readonly HeaderReader _main;
    private readonly bool R13_14Only;
    private readonly bool R2004Pre;
    private readonly bool R2004Plus;
    private readonly bool R2000Plus;
    private readonly bool R2007Plus;
    private readonly bool R2010Plus;
    private readonly bool R2013Plus;
    private readonly bool R13_15Only;

    public Dictionary<string, double> Numbers { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

    private DwgHeaderVariables(DwgBits bits, int generation)
    {
        _main = new HeaderReader(bits, generation, handlesInline: true);
        _reader = new HeaderReader(bits, generation, handlesInline: generation < 21);
        R13_14Only = generation < 15;
        R13_15Only = generation <= 15;
        R2004Pre = generation < 18;
        R2004Plus = generation >= 18;
        R2000Plus = generation >= 15;
        R2007Plus = generation >= 21;
        R2010Plus = generation >= 24;
        R2013Plus = generation >= 27;
    }

    /// <summary>
    /// Decodes the variables of a header section (sentinel, size, then the bit stream). Returns null when the section does not start the way every release
    /// writes it (its first double is 412148564080.0), which is also how the optional extra length field of AC1024 and AC1027 is told apart.
    /// </summary>
    public static DwgHeaderVariables? Read(byte[] section, int generation)
    {
        if (generation < 15 || section.Length < 40)
        {
            return null;
        }

        foreach (var extra in generation >= 24 ? new[] { 0, 4 } : new[] { 0 })
        {
            var start = (16 + 4 + extra) * 8L;
            var end = section.Length * 8L;
            var bits = new DwgBits(section, start, end);
            if (generation >= 21)
            {
                var sizeInBits = bits.RL();
                bits.OpenStrings(start + sizeInBits);
            }

            var probe = new DwgBits(section, bits.Position, end) { Strings = null };
            if (generation >= 27)
            {
                probe.BLL();
            }

            if (Math.Abs(probe.BD() - 412148564080.0) > 1)
            {
                continue;
            }

            var result = new DwgHeaderVariables(bits, generation);
            try
            {
                result.Run();
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
            {
                return result.Numbers.Count > 0 ? result : null;
            }

            return result;
        }

        return null;
    }

    private double Num(string name) => Numbers.GetValueOrDefault(name);

    private void Set(string name, object value)
    {
        switch (value)
        {
            case bool b:
                Numbers[name] = b ? 1 : 0;
                break;
            case string s:
                Texts[name] = s;
                break;
            case (double x, double y, double z):
                Numbers[name + "X"] = x;
                Numbers[name + "Y"] = y;
                Numbers[name + "Z"] = z;
                break;
            case (double x, double y):
                Numbers[name + "X"] = x;
                Numbers[name + "Y"] = y;
                break;
            case char c:
                Numbers[name] = c;
                break;
            default:
                Numbers[name] = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
        }
    }

    private void Run()
    {
        if (R2013Plus)
        Set("RequiredVersions", _reader.ReadBitLongLong());
        double unknownbd1 = _reader.ReadBitDouble();
        double unknownbd2 = _reader.ReadBitDouble();
        double unknownbd3 = _reader.ReadBitDouble();
        double unknownbd4 = _reader.ReadBitDouble();
        string unknowntv = _reader.ReadVariableText();
        unknowntv = _reader.ReadVariableText();
        unknowntv = _reader.ReadVariableText();
        unknowntv = _reader.ReadVariableText();
        var unknownbl = _reader.ReadBitLong();
        unknownbl = _reader.ReadBitLong();
        if (R13_14Only)
        {
            short unknowns = _reader.ReadBitShort();
        }
        if (R2004Pre)
        {
            ulong pointerViewPort = _reader.HandleReference();
        }
        Set("AssociatedDimensions", _reader.ReadBit());
        Set("UpdateDimensionsWhileDragging", _reader.ReadBit());
        if (R13_14Only)
        {
            Set("DIMSAV", _reader.ReadBit());
        }
        Set("PolylineLineTypeGeneration", _reader.ReadBit());
        Set("OrthoMode", _reader.ReadBit());
        Set("RegenerationMode", _reader.ReadBit());
        Set("FillMode", _reader.ReadBit());
        Set("QuickTextMode", _reader.ReadBit());
        Set("PaperSpaceLineTypeScaling", _reader.ReadBit() );
        Set("LimitCheckingOn", _reader.ReadBit());
        if (R13_14Only)
        Set("BlipMode", _reader.ReadBit());
        if (R2004Plus)
        _reader.ReadBit();
        Set("UserTimer", _reader.ReadBit());
        Set("SketchPolylines", _reader.ReadBit());
        Set("AngularDirection", _reader.ReadBitAsShort());
        Set("ShowSplineControlPoints", _reader.ReadBit());
        if (R13_14Only)
        {
            _reader.ReadBit();
            _reader.ReadBit();
        }
        Set("MirrorText", _reader.ReadBit());
        Set("WorldView", _reader.ReadBit());
        if (R13_14Only)
        _reader.ReadBit();
        Set("ShowModelSpace", _reader.ReadBit());
        Set("PaperSpaceLimitsChecking", _reader.ReadBit());
        Set("RetainXRefDependentVisibilitySettings", _reader.ReadBit());
        if (R13_14Only)
        _reader.ReadBit();
        Set("DisplaySilhouetteCurves", _reader.ReadBit());
        Set("CreateEllipseAsPolyline", _reader.ReadBit());
        Set("ProxyGraphics", _reader.ReadBitShortAsBool());
        if (R13_14Only)
        {
            _reader.ReadBitShort();
        }
        Set("SpatialIndexMaxTreeDepth", _reader.ReadBitShort());
        Set("LinearUnitFormat", _reader.ReadBitShort());
        var linearUnitPrecision = _reader.ReadBitShort();
        if (linearUnitPrecision >= 0 && linearUnitPrecision <= 8)
        {
            Set("LinearUnitPrecision", linearUnitPrecision);
        }
        Set("AngularUnit", _reader.ReadBitShort());
        var angularUnitPrecision = _reader.ReadBitShort();
        if (angularUnitPrecision >= 0 && angularUnitPrecision <= 8)
        {
            Set("AngularUnitPrecision", angularUnitPrecision);
        }
        if (R13_14Only)
        {
            Set("ObjectSnapMode", _reader.ReadBitShort());
        }
        Set("AttributeVisibility", _reader.ReadBitShort());
        if (R13_14Only)
        _reader.ReadBitShort();
        Set("PointDisplayMode", _reader.ReadBitShort());
        if (R13_14Only)
        _reader.ReadBitShort();
        if (R2004Plus)
        {
            _reader.ReadBitLong();
            _reader.ReadBitLong();
            _reader.ReadBitLong();
        }
        Set("UserShort1", _reader.ReadBitShort());
        Set("UserShort2", _reader.ReadBitShort());
        Set("UserShort3", _reader.ReadBitShort());
        Set("UserShort4", _reader.ReadBitShort());
        Set("UserShort5", _reader.ReadBitShort());
        Set("NumberOfSplineSegments", _reader.ReadBitShort());
        Set("SurfaceDensityU", _reader.ReadBitShort());
        Set("SurfaceDensityV", _reader.ReadBitShort());
        Set("SurfaceType", _reader.ReadBitShort());
        Set("SurfaceMeshTabulationCount1", _reader.ReadBitShort());
        Set("SurfaceMeshTabulationCount2", _reader.ReadBitShort());
        Set("SplineType", _reader.ReadBitShort());
        Set("ShadeEdge", _reader.ReadBitShort());
        Set("ShadeDiffuseToAmbientPercentage", _reader.ReadBitShort());
        Set("UnitMode", _reader.ReadBitShort());
        Set("MaxViewportCount", _reader.ReadBitShort());
        var surfaceIsoLineCount = _reader.ReadBitShort();
        if (surfaceIsoLineCount >= 0 && surfaceIsoLineCount <= 2047)
        {
            Set("SurfaceIsolineCount", surfaceIsoLineCount);
        }
        Set("CurrentMultiLineJustification", _reader.ReadBitShort());
        var textQuality = _reader.ReadBitShort();
        if (textQuality >= 0 && textQuality <= 100)
        {
            Set("TextQuality", textQuality);
        }
        Set("LineTypeScale", _reader.ReadBitDouble());
        Set("TextHeightDefault", _reader.ReadBitDouble());
        Set("TraceWidthDefault", _reader.ReadBitDouble());
        Set("SketchIncrement", _reader.ReadBitDouble());
        Set("FilletRadius", _reader.ReadBitDouble());
        Set("ThicknessDefault", _reader.ReadBitDouble());
        Set("AngleBase", _reader.ReadBitDouble());
        Set("PointDisplaySize", _reader.ReadBitDouble());
        Set("PolylineWidthDefault", _reader.ReadBitDouble());
        Set("UserDouble1", _reader.ReadBitDouble());
        Set("UserDouble2", _reader.ReadBitDouble());
        Set("UserDouble3", _reader.ReadBitDouble());
        Set("UserDouble4", _reader.ReadBitDouble());
        Set("UserDouble5", _reader.ReadBitDouble());
        Set("ChamferDistance1", _reader.ReadBitDouble());
        Set("ChamferDistance2", _reader.ReadBitDouble());
        Set("ChamferLength", _reader.ReadBitDouble());
        Set("ChamferAngle", _reader.ReadBitDouble());
        var facetResolution = _reader.ReadBitDouble();
        if (facetResolution > 0 && facetResolution <= 10)
        {
            Set("FacetResolution", facetResolution);
        }
        Set("CurrentMultilineScale", _reader.ReadBitDouble());
        Set("CurrentEntityLinetypeScale", _reader.ReadBitDouble());
        Set("MenuFileName", _reader.ReadVariableText());
        Set("CreateDateTime", _reader.ReadDateTime());
        Set("UpdateDateTime", _reader.ReadDateTime());
        if (R2004Plus)
        {
            _reader.ReadBitLong();
            _reader.ReadBitLong();
            _reader.ReadBitLong();
        }
        Set("TotalEditingTime", _reader.ReadTimeSpan());
        Set("UserElapsedTimeSpan", _reader.ReadTimeSpan());
        Set("CurrentEntityColor", _reader.ReadCmColor());
        Set("HandleSeed", _main.HandleReference());
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        if (R2007Plus)
        {
            _reader.HandleReference();
        }
        _reader.HandleReference();
        _reader.HandleReference();
        if (R2000Plus)
        {
            Set("ViewportDefaultViewScaleFactor", _reader.ReadBitDouble());
        }
        Set("PaperSpaceInsertionBase", _reader.Read3BitDouble());
        Set("PaperSpaceExtMin", _reader.Read3BitDouble());
        Set("PaperSpaceExtMax", _reader.Read3BitDouble());
        Set("PaperSpaceLimitsMin", _reader.Read2RawDouble());
        Set("PaperSpaceLimitsMax", _reader.Read2RawDouble());
        Set("PaperSpaceElevation", _reader.ReadBitDouble());
        Set("PaperSpaceUcsOrigin", _reader.Read3BitDouble());
        Set("PaperSpaceUcsXAxis", _reader.Read3BitDouble());
        Set("PaperSpaceUcsYAxis", _reader.Read3BitDouble());
        _reader.HandleReference();
        if (R2000Plus)
        {
            _reader.HandleReference();
            int PUCSORTHOVIEW = _reader.ReadBitShort();
            _reader.HandleReference();
            Set("PaperSpaceOrthographicTopDOrigin", _reader.Read3BitDouble());
            Set("PaperSpaceOrthographicBottomDOrigin", _reader.Read3BitDouble());
            Set("PaperSpaceOrthographicLeftDOrigin", _reader.Read3BitDouble());
            Set("PaperSpaceOrthographicRightDOrigin", _reader.Read3BitDouble());
            Set("PaperSpaceOrthographicFrontDOrigin", _reader.Read3BitDouble());
            Set("PaperSpaceOrthographicBackDOrigin", _reader.Read3BitDouble());
        }
        Set("ModelSpaceInsertionBase", _reader.Read3BitDouble());
        Set("ModelSpaceExtMin", _reader.Read3BitDouble());
        Set("ModelSpaceExtMax", _reader.Read3BitDouble());
        Set("ModelSpaceLimitsMin", _reader.Read2RawDouble());
        Set("ModelSpaceLimitsMax", _reader.Read2RawDouble());
        Set("Elevation", _reader.ReadBitDouble());
        Set("ModelSpaceOrigin", _reader.Read3BitDouble());
        Set("ModelSpaceXAxis", _reader.Read3BitDouble());
        Set("ModelSpaceYAxis", _reader.Read3BitDouble());
        _reader.HandleReference();
        if (R2000Plus)
        {
            _reader.HandleReference();
            short UCSORTHOVIEW = _reader.ReadBitShort();
            _reader.HandleReference();
            Set("ModelSpaceOrthographicTopDOrigin", _reader.Read3BitDouble());
            Set("ModelSpaceOrthographicBottomDOrigin", _reader.Read3BitDouble());
            Set("ModelSpaceOrthographicLeftDOrigin", _reader.Read3BitDouble());
            Set("ModelSpaceOrthographicRightDOrigin", _reader.Read3BitDouble());
            Set("ModelSpaceOrthographicFrontDOrigin", _reader.Read3BitDouble());
            Set("ModelSpaceOrthographicBackDOrigin", _reader.Read3BitDouble());
            Set("DimensionPostFix", _reader.ReadVariableText());
            Set("DimensionAlternateDimensioningSuffix", _reader.ReadVariableText());
        }
        if (R13_14Only)
        {
            Set("DimensionGenerateTolerances", _reader.ReadBit());
            Set("DimensionLimitsGeneration", _reader.ReadBit());
            Set("DimensionTextInsideHorizontal", _reader.ReadBit());
            Set("DimensionTextOutsideHorizontal", _reader.ReadBit());
            Set("DimensionSuppressFirstExtensionLine", _reader.ReadBit());
            Set("DimensionSuppressSecondExtensionLine", _reader.ReadBit());
            Set("DimensionAlternateUnitDimensioning", _reader.ReadBit());
            Set("DimensionTextOutsideExtensions", _reader.ReadBit());
            Set("DimensionSeparateArrowBlocks", _reader.ReadBit());
            Set("DimensionTextInsideExtensions", _reader.ReadBit());
            Set("DimensionSuppressOutsideExtensions", _reader.ReadBit());
            Set("DimensionAlternateUnitDecimalPlaces", _reader.ReadRawChar());
            Set("DimensionZeroHandling", _reader.ReadRawChar());
            Set("DimensionSuppressFirstDimensionLine", _reader.ReadBit());
            Set("DimensionSuppressSecondDimensionLine", _reader.ReadBit());
            Set("DimensionToleranceAlignment", _reader.ReadRawChar());
            Set("DimensionTextHorizontalAlignment", _reader.ReadRawChar());
            Set("DimensionFit", _reader.ReadRawChar());
            Set("DimensionCursorUpdate", _reader.ReadBit());
            Set("DimensionToleranceZeroHandling", _reader.ReadRawChar());
            Set("DimensionAlternateUnitZeroHandling", _reader.ReadRawChar());
            Set("DimensionAlternateUnitToleranceZeroHandling", _reader.ReadRawChar());
            Set("DimensionTextVerticalAlignment", _reader.ReadRawChar());
            Set("DimensionUnit", _reader.ReadBitShort());
            Set("DimensionAngularDimensionDecimalPlaces", _reader.ReadBitShort());
            Set("DimensionDecimalPlaces", _reader.ReadBitShort());
            Set("DimensionToleranceDecimalPlaces", _reader.ReadBitShort());
            Set("DimensionAlternateUnitFormat", _reader.ReadBitShort());
            Set("DimensionAlternateUnitToleranceDecimalPlaces", _reader.ReadBitShort());
            _reader.HandleReference();
        }
        Set("DimensionScaleFactor", _reader.ReadBitDouble());
        Set("DimensionArrowSize", _reader.ReadBitDouble());
        Set("DimensionExtensionLineOffset", _reader.ReadBitDouble());
        Set("DimensionLineIncrement", _reader.ReadBitDouble());
        Set("DimensionExtensionLineExtension", _reader.ReadBitDouble());
        Set("DimensionRounding", _reader.ReadBitDouble());
        Set("DimensionLineExtension", _reader.ReadBitDouble());
        Set("DimensionPlusTolerance", _reader.ReadBitDouble());
        Set("DimensionMinusTolerance", _reader.ReadBitDouble());
        if (R2007Plus)
        {
            Set("DimensionFixedExtensionLineLength", _reader.ReadBitDouble());
            var dimensionJoggedRadiusDimensionTransverseSegmentAngle = _reader.ReadBitDouble();
            {
                Set("DimensionJoggedRadiusDimensionTransverseSegmentAngle", dimensionJoggedRadiusDimensionTransverseSegmentAngle);
            }
            Set("DimensionTextBackgroundFillMode", _reader.ReadBitShort());
            Set("DimensionTextBackgroundColor", _reader.ReadCmColor());
        }
        if (R2000Plus)
        {
            Set("DimensionGenerateTolerances", _reader.ReadBit());
            Set("DimensionLimitsGeneration", _reader.ReadBit());
            Set("DimensionTextInsideHorizontal", _reader.ReadBit());
            Set("DimensionTextOutsideHorizontal", _reader.ReadBit());
            Set("DimensionSuppressFirstExtensionLine", _reader.ReadBit());
            Set("DimensionSuppressSecondExtensionLine", _reader.ReadBit());
            Set("DimensionTextVerticalAlignment", _reader.ReadBitShort());
            Set("DimensionZeroHandling", _reader.ReadBitShort());
            Set("DimensionAngularZeroHandling", _reader.ReadBitShort());
        }
        if (R2007Plus)
        {
            Set("DimensionArcLengthSymbolPosition", _reader.ReadBitShort());
        }
        Set("DimensionTextHeight", _reader.ReadBitDouble());
        Set("DimensionCenterMarkSize", _reader.ReadBitDouble());
        Set("DimensionTickSize", _reader.ReadBitDouble());
        Set("DimensionAlternateUnitScaleFactor", _reader.ReadBitDouble());
        Set("DimensionLinearScaleFactor", _reader.ReadBitDouble());
        Set("DimensionTextVerticalPosition", _reader.ReadBitDouble());
        Set("DimensionToleranceScaleFactor", _reader.ReadBitDouble());
        Set("DimensionLineGap", _reader.ReadBitDouble());
        if (R13_14Only)
        {
            Set("DimensionPostFix", _reader.ReadVariableText());
            Set("DimensionAlternateDimensioningSuffix", _reader.ReadVariableText());
            Set("DimensionBlockName", _reader.ReadVariableText());
            Set("DimensionBlockNameFirst", _reader.ReadVariableText());
            Set("DimensionBlockNameSecond", _reader.ReadVariableText());
        }
        if (R2000Plus)
        {
            Set("DimensionAlternateUnitRounding", _reader.ReadBitDouble());
            Set("DimensionAlternateUnitDimensioning", _reader.ReadBit());
            Set("DimensionAlternateUnitDecimalPlaces", _reader.ReadBitShort());
            Set("DimensionTextOutsideExtensions", _reader.ReadBit());
            Set("DimensionSeparateArrowBlocks", _reader.ReadBit());
            Set("DimensionTextInsideExtensions", _reader.ReadBit());
            Set("DimensionSuppressOutsideExtensions", _reader.ReadBit());
        }
        Set("DimensionLineColor", _reader.ReadCmColor());
        Set("DimensionExtensionLineColor", _reader.ReadCmColor());
        Set("DimensionTextColor", _reader.ReadCmColor());
        if (R2000Plus)
        {
            Set("DimensionAngularDimensionDecimalPlaces", _reader.ReadBitShort());
            Set("DimensionDecimalPlaces", _reader.ReadBitShort());
            Set("DimensionToleranceDecimalPlaces", _reader.ReadBitShort());
            Set("DimensionAlternateUnitFormat", _reader.ReadBitShort());
            Set("DimensionAlternateUnitToleranceDecimalPlaces", _reader.ReadBitShort());
            Set("DimensionAngularUnit", _reader.ReadBitShort());
            Set("DimensionFractionFormat", _reader.ReadBitShort());
            Set("DimensionLinearUnitFormat", _reader.ReadBitShort());
            Set("DimensionDecimalSeparator", _reader.ReadBitShort());
            Set("DimensionTextMovement", _reader.ReadBitShort());
            Set("DimensionTextHorizontalAlignment", _reader.ReadBitShort());
            Set("DimensionSuppressFirstExtensionLine", _reader.ReadBit());
            Set("DimensionSuppressSecondExtensionLine", _reader.ReadBit());
            Set("DimensionToleranceAlignment", _reader.ReadBitShort());
            Set("DimensionToleranceZeroHandling", _reader.ReadBitShort());
            Set("DimensionAlternateUnitZeroHandling", _reader.ReadBitShort());
            Set("DimensionAlternateUnitToleranceZeroHandling", _reader.ReadBitShort());
            Set("DimensionCursorUpdate", _reader.ReadBit());
            Set("DimensionDimensionTextArrowFit", _reader.ReadBitShort());
        }
        if (R2007Plus)
        {
            Set("DimensionIsExtensionLineLengthFixed", _reader.ReadBit());
        }
        if (R2010Plus)
        {
            Set("DimensionTextDirection", _reader.ReadBit() );
            Set("DimensionAltMzf", _reader.ReadBitDouble());
            Set("DimensionAltMzs", _reader.ReadVariableText());
            Set("DimensionMzf", _reader.ReadBitDouble());
            Set("DimensionMzs", _reader.ReadVariableText());
        }
        if (R2000Plus)
        {
            _reader.HandleReference();
            _reader.HandleReference();
            _reader.HandleReference();
            _reader.HandleReference();
            _reader.HandleReference();
        }
        if (R2007Plus)
        {
            _reader.HandleReference();
            _reader.HandleReference();
            _reader.HandleReference();
        }
        if (R2000Plus)
        {
            Set("DimensionLineWeight", _reader.ReadBitShort());
            Set("ExtensionLineWeight", _reader.ReadBitShort());
        }
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        if (R13_15Only)
        {
            _reader.HandleReference();
        }
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        if (R2000Plus)
        {
            Set("StackedTextAlignment", _reader.ReadBitShort());
            Set("StackedTextSizePercentage", _reader.ReadBitShort());
            Set("HyperLinkBase", _reader.ReadVariableText());
            Set("StyleSheetName", _reader.ReadVariableText());
            _reader.HandleReference();
            _reader.HandleReference();
            _reader.HandleReference();
        }
        if (R2004Plus)
        {
            _reader.HandleReference();
            _reader.HandleReference();
        }
        if (R2007Plus)
        {
            _reader.HandleReference();
            if (R2013Plus)
            _reader.HandleReference();
        }
        if (R2000Plus)
        {
            int flags = _reader.ReadBitLong();
            Set("CurrentEntityLineWeight", (flags & 0x1F));
            Set("EndCaps", (flags & 0x60));
            Set("JoinStyle", (flags & 0x180));
            Set("DisplayLineWeight", (flags & 0x200) == 1);
            Set("XEdit", (flags & 0x400) == 1);
            Set("ExtendedNames", (flags & 0x800) == 1);
            Set("PlotStyleMode", (flags & 0x2000));
            Set("LoadOLEObject", (flags & 0x4000) == 1);
            Set("InsUnits", _reader.ReadBitShort());
            Set("CurrentEntityPlotStyle", _reader.ReadBitShort());
            if (Num("CurrentEntityPlotStyle") == 3)
            {
                _reader.HandleReference();
            }
            Set("FingerPrintGuid", _reader.ReadVariableText());
            Set("VersionGuid", _reader.ReadVariableText());
        }
        if (R2004Plus)
        {
            Set("EntitySortingFlags", _reader.ReadByte());
            Set("IndexCreationFlags", _reader.ReadByte());
            Set("HideText", _reader.ReadByte());
            Set("ExternalReferenceClippingBoundaryType", _reader.ReadByte());
            Set("DimensionAssociativity", _reader.ReadByte());
            Set("HaloGapPercentage", _reader.ReadByte());
            Set("ObscuredColor", _reader.ReadBitShort());
            Set("InterfereColor", _reader.ReadBitShort());
            Set("ObscuredType", _reader.ReadByte());
            Set("IntersectionDisplay", _reader.ReadByte());
            Set("ProjectName", _reader.ReadVariableText());
        }
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        _reader.HandleReference();
        if (R2007Plus)
        {
            Set("CameraDisplayObjects", _reader.ReadBit());
            _reader.ReadBitLong();
            _reader.ReadBitLong();
            _reader.ReadBitDouble();
            var stepsPerSecond = _reader.ReadBitDouble();
            if (stepsPerSecond >= 1 && stepsPerSecond <= 30)
            {
                Set("StepsPerSecond", stepsPerSecond);
            }
            Set("StepSize", _reader.ReadBitDouble());
            Set("Dw3DPrecision", _reader.ReadBitDouble());
            Set("LensLength", _reader.ReadBitDouble());
            Set("CameraHeight", _reader.ReadBitDouble());
            Set("SolidsRetainHistory", _reader.ReadRawChar());
            Set("ShowSolidsHistory", _reader.ReadRawChar());
            Set("SweptSolidWidth", _reader.ReadBitDouble());
            Set("SweptSolidHeight", _reader.ReadBitDouble());
            Set("DraftAngleFirstCrossSection", _reader.ReadBitDouble());
            Set("DraftAngleSecondCrossSection", _reader.ReadBitDouble());
            Set("DraftMagnitudeFirstCrossSection", _reader.ReadBitDouble());
            Set("DraftMagnitudeSecondCrossSection", _reader.ReadBitDouble());
            Set("SolidLoftedShape", _reader.ReadBitShort());
            Set("LoftedObjectNormals", _reader.ReadRawChar());
            Set("Latitude", _reader.ReadBitDouble());
            Set("Longitude", _reader.ReadBitDouble());
            Set("NorthDirection", _reader.ReadBitDouble());
            Set("TimeZone", _reader.ReadBitLong());
            Set("DisplayLightGlyphs", _reader.ReadRawChar());
            _reader.ReadRawChar();
            Set("DwgUnderlayFramesVisibility", _reader.ReadRawChar());
            Set("DgnUnderlayFramesVisibility", _reader.ReadRawChar());
            _reader.ReadBit();
            Set("InterfereColor", _reader.ReadCmColor());
            _reader.HandleReference();
            _reader.HandleReference();
            _reader.HandleReference();
            Set("ShadowMode", _reader.ReadByte());
            Set("ShadowPlaneLocation", _reader.ReadBitDouble());
        }
    }

    /// <summary>The reading operations of the header reader the converted code uses, over the bit stream.</summary>
    private sealed class HeaderReader
    {
        private readonly DwgBits _b;
        private readonly int _generation;
        private readonly bool _handlesInline;

        public HeaderReader(DwgBits bits, int generation, bool handlesInline)
        {
            _b = bits;
            _generation = generation;
            _handlesInline = handlesInline;
        }

        public short ReadBitShort() => (short)_b.BS();

        public double ReadBitDouble() => _b.BD();

        public bool ReadBit() => _b.Bit();

        public short ReadBitAsShort() => (short)(_b.Bit() ? 1 : 0);

        public bool ReadBitShortAsBool() => _b.BS() != 0;

        public string ReadVariableText() => _b.TV();

        public (double, double, double) Read3BitDouble() => _b.BD3();

        public (double, double) Read2RawDouble() => _b.RD2();

        public int ReadBitLong() => _b.BL();

        public long ReadBitLongLong() => _b.BLL();

        public byte ReadByte() => (byte)_b.RC();

        public char ReadRawChar() => (char)_b.RC();

        public int ReadRawLong() => (int)_b.RL();

        public double ReadDateTime()
        {
            var day = _b.BL();
            var milliseconds = _b.BL();
            return day + milliseconds / 86_400_000.0;
        }

        public double ReadTimeSpan() => ReadDateTime();

        public int ReadCmColor()
        {
            var index = _b.BS();
            if (_generation >= 18)
            {
                _ = _b.BL();
                var flag = _b.RC();
                if ((flag & 1) != 0)
                {
                    _ = _b.TV();
                }

                if ((flag & 2) != 0)
                {
                    _ = _b.TV();
                }
            }

            return index;
        }

        public ulong HandleReference() => _handlesInline ? (ulong)_b.Handle(0) : 0;
    }
}
