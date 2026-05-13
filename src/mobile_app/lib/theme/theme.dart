import 'package:flutter/material.dart';

class MaterialTheme {
  final TextTheme textTheme;

  const MaterialTheme(this.textTheme);

  static ColorScheme lightScheme() {
    return const ColorScheme(
      brightness: Brightness.light,
      primary: Color(0xff2c638b),
      surfaceTint: Color(0xff2c638b),
      onPrimary: Color(0xffffffff),
      primaryContainer: Color(0xffcce5ff),
      onPrimaryContainer: Color(0xff084b72),
      secondary: Color(0xff51606f),
      onSecondary: Color(0xffffffff),
      secondaryContainer: Color(0xffd4e4f6),
      onSecondaryContainer: Color(0xff394857),
      tertiary: Color(0xff67587a),
      onTertiary: Color(0xffffffff),
      tertiaryContainer: Color(0xffeddcff),
      onTertiaryContainer: Color(0xff4e4161),
      error: Color(0xffba1a1a),
      onError: Color(0xffffffff),
      errorContainer: Color(0xffffdad6),
      onErrorContainer: Color(0xff93000a),
      surface: Color(0xfff7f9ff),
      onSurface: Color(0xff181c20),
      onSurfaceVariant: Color(0xff42474e),
      outline: Color(0xff72787e),
      outlineVariant: Color(0xffc2c7ce),
      shadow: Color(0xff000000),
      scrim: Color(0xff000000),
      inverseSurface: Color(0xff2d3135),
      inversePrimary: Color(0xff99ccfa),
      primaryFixed: Color(0xffcce5ff),
      onPrimaryFixed: Color(0xff001d31),
      primaryFixedDim: Color(0xff99ccfa),
      onPrimaryFixedVariant: Color(0xff084b72),
      secondaryFixed: Color(0xffd4e4f6),
      onSecondaryFixed: Color(0xff0d1d2a),
      secondaryFixedDim: Color(0xffb8c8da),
      onSecondaryFixedVariant: Color(0xff394857),
      tertiaryFixed: Color(0xffeddcff),
      onTertiaryFixed: Color(0xff221533),
      tertiaryFixedDim: Color(0xffd2bfe7),
      onTertiaryFixedVariant: Color(0xff4e4161),
      surfaceDim: Color(0xffd7dadf),
      surfaceBright: Color(0xfff7f9ff),
      surfaceContainerLowest: Color(0xffffffff),
      surfaceContainerLow: Color(0xfff1f4f9),
      surfaceContainer: Color(0xffebeef3),
      surfaceContainerHigh: Color(0xffe6e8ee),
      surfaceContainerHighest: Color(0xffe0e2e8),
    );
  }

  ThemeData light() {
    return theme(lightScheme());
  }

  static ColorScheme lightMediumContrastScheme() {
    return const ColorScheme(
      brightness: Brightness.light,
      primary: Color(0xff00395a),
      surfaceTint: Color(0xff2c638b),
      onPrimary: Color(0xffffffff),
      primaryContainer: Color(0xff3d719b),
      onPrimaryContainer: Color(0xffffffff),
      secondary: Color(0xff293845),
      onSecondary: Color(0xffffffff),
      secondaryContainer: Color(0xff5f6f7e),
      onSecondaryContainer: Color(0xffffffff),
      tertiary: Color(0xff3d3050),
      onTertiary: Color(0xffffffff),
      tertiaryContainer: Color(0xff76668a),
      onTertiaryContainer: Color(0xffffffff),
      error: Color(0xff740006),
      onError: Color(0xffffffff),
      errorContainer: Color(0xffcf2c27),
      onErrorContainer: Color(0xffffffff),
      surface: Color(0xfff7f9ff),
      onSurface: Color(0xff0e1215),
      onSurfaceVariant: Color(0xff31373d),
      outline: Color(0xff4d5359),
      outlineVariant: Color(0xff686e74),
      shadow: Color(0xff000000),
      scrim: Color(0xff000000),
      inverseSurface: Color(0xff2d3135),
      inversePrimary: Color(0xff99ccfa),
      primaryFixed: Color(0xff3d719b),
      onPrimaryFixed: Color(0xffffffff),
      primaryFixedDim: Color(0xff205981),
      onPrimaryFixedVariant: Color(0xffffffff),
      secondaryFixed: Color(0xff5f6f7e),
      onSecondaryFixed: Color(0xffffffff),
      secondaryFixedDim: Color(0xff475665),
      onSecondaryFixedVariant: Color(0xffffffff),
      tertiaryFixed: Color(0xff76668a),
      onTertiaryFixed: Color(0xffffffff),
      tertiaryFixedDim: Color(0xff5d4e70),
      onTertiaryFixedVariant: Color(0xffffffff),
      surfaceDim: Color(0xffc4c7cc),
      surfaceBright: Color(0xfff7f9ff),
      surfaceContainerLowest: Color(0xffffffff),
      surfaceContainerLow: Color(0xfff1f4f9),
      surfaceContainer: Color(0xffe6e8ee),
      surfaceContainerHigh: Color(0xffdadde2),
      surfaceContainerHighest: Color(0xffcfd2d7),
    );
  }

  ThemeData lightMediumContrast() {
    return theme(lightMediumContrastScheme());
  }

  static ColorScheme lightHighContrastScheme() {
    return const ColorScheme(
      brightness: Brightness.light,
      primary: Color(0xff002f4b),
      surfaceTint: Color(0xff2c638b),
      onPrimary: Color(0xffffffff),
      primaryContainer: Color(0xff0d4d74),
      onPrimaryContainer: Color(0xffffffff),
      secondary: Color(0xff1e2e3b),
      onSecondary: Color(0xffffffff),
      secondaryContainer: Color(0xff3c4b59),
      onSecondaryContainer: Color(0xffffffff),
      tertiary: Color(0xff332645),
      onTertiary: Color(0xffffffff),
      tertiaryContainer: Color(0xff514364),
      onTertiaryContainer: Color(0xffffffff),
      error: Color(0xff600004),
      onError: Color(0xffffffff),
      errorContainer: Color(0xff98000a),
      onErrorContainer: Color(0xffffffff),
      surface: Color(0xfff7f9ff),
      onSurface: Color(0xff000000),
      onSurfaceVariant: Color(0xff000000),
      outline: Color(0xff272d33),
      outlineVariant: Color(0xff444a50),
      shadow: Color(0xff000000),
      scrim: Color(0xff000000),
      inverseSurface: Color(0xff2d3135),
      inversePrimary: Color(0xff99ccfa),
      primaryFixed: Color(0xff0d4d74),
      onPrimaryFixed: Color(0xffffffff),
      primaryFixedDim: Color(0xff003655),
      onPrimaryFixedVariant: Color(0xffffffff),
      secondaryFixed: Color(0xff3c4b59),
      onSecondaryFixed: Color(0xffffffff),
      secondaryFixedDim: Color(0xff253442),
      onSecondaryFixedVariant: Color(0xffffffff),
      tertiaryFixed: Color(0xff514364),
      onTertiaryFixed: Color(0xffffffff),
      tertiaryFixedDim: Color(0xff3a2d4c),
      onTertiaryFixedVariant: Color(0xffffffff),
      surfaceDim: Color(0xffb6b9be),
      surfaceBright: Color(0xfff7f9ff),
      surfaceContainerLowest: Color(0xffffffff),
      surfaceContainerLow: Color(0xffeef1f6),
      surfaceContainer: Color(0xffe0e2e8),
      surfaceContainerHigh: Color(0xffd2d4da),
      surfaceContainerHighest: Color(0xffc4c7cc),
    );
  }

  ThemeData lightHighContrast() {
    return theme(lightHighContrastScheme());
  }

  static ColorScheme darkScheme() {
    return const ColorScheme(
      brightness: Brightness.dark,
      primary: Color(0xff99ccfa),
      surfaceTint: Color(0xff99ccfa),
      onPrimary: Color(0xff003351),
      primaryContainer: Color(0xff084b72),
      onPrimaryContainer: Color(0xffcce5ff),
      secondary: Color(0xffb8c8da),
      onSecondary: Color(0xff23323f),
      secondaryContainer: Color(0xff394857),
      onSecondaryContainer: Color(0xffd4e4f6),
      tertiary: Color(0xffd2bfe7),
      onTertiary: Color(0xff372a4a),
      tertiaryContainer: Color(0xff4e4161),
      onTertiaryContainer: Color(0xffeddcff),
      error: Color(0xffffb4ab),
      onError: Color(0xff690005),
      errorContainer: Color(0xff93000a),
      onErrorContainer: Color(0xffffdad6),
      surface: Color(0xff101418),
      onSurface: Color(0xffe0e2e8),
      onSurfaceVariant: Color(0xffc2c7ce),
      outline: Color(0xff8c9198),
      outlineVariant: Color(0xff42474e),
      shadow: Color(0xff000000),
      scrim: Color(0xff000000),
      inverseSurface: Color(0xffe0e2e8),
      inversePrimary: Color(0xff2c638b),
      primaryFixed: Color(0xffcce5ff),
      onPrimaryFixed: Color(0xff001d31),
      primaryFixedDim: Color(0xff99ccfa),
      onPrimaryFixedVariant: Color(0xff084b72),
      secondaryFixed: Color(0xffd4e4f6),
      onSecondaryFixed: Color(0xff0d1d2a),
      secondaryFixedDim: Color(0xffb8c8da),
      onSecondaryFixedVariant: Color(0xff394857),
      tertiaryFixed: Color(0xffeddcff),
      onTertiaryFixed: Color(0xff221533),
      tertiaryFixedDim: Color(0xffd2bfe7),
      onTertiaryFixedVariant: Color(0xff4e4161),
      surfaceDim: Color(0xff101418),
      surfaceBright: Color(0xff36393e),
      surfaceContainerLowest: Color(0xff0b0f12),
      surfaceContainerLow: Color(0xff181c20),
      surfaceContainer: Color(0xff1c2024),
      surfaceContainerHigh: Color(0xff272a2e),
      surfaceContainerHighest: Color(0xff313539),
    );
  }

  ThemeData dark() {
    return theme(darkScheme());
  }

  static ColorScheme darkMediumContrastScheme() {
    return const ColorScheme(
      brightness: Brightness.dark,
      primary: Color(0xffc0e0ff),
      surfaceTint: Color(0xff99ccfa),
      onPrimary: Color(0xff002841),
      primaryContainer: Color(0xff6395c1),
      onPrimaryContainer: Color(0xff000000),
      secondary: Color(0xffcedef0),
      onSecondary: Color(0xff182734),
      secondaryContainer: Color(0xff8392a3),
      onSecondaryContainer: Color(0xff000000),
      tertiary: Color(0xffe8d5fe),
      onTertiary: Color(0xff2c203e),
      tertiaryContainer: Color(0xff9a8aaf),
      onTertiaryContainer: Color(0xff000000),
      error: Color(0xffffd2cc),
      onError: Color(0xff540003),
      errorContainer: Color(0xffff5449),
      onErrorContainer: Color(0xff000000),
      surface: Color(0xff101418),
      onSurface: Color(0xffffffff),
      onSurfaceVariant: Color(0xffd8dde4),
      outline: Color(0xffadb2ba),
      outlineVariant: Color(0xff8b9198),
      shadow: Color(0xff000000),
      scrim: Color(0xff000000),
      inverseSurface: Color(0xffe0e2e8),
      inversePrimary: Color(0xff0a4c73),
      primaryFixed: Color(0xffcce5ff),
      onPrimaryFixed: Color(0xff001321),
      primaryFixedDim: Color(0xff99ccfa),
      onPrimaryFixedVariant: Color(0xff00395a),
      secondaryFixed: Color(0xffd4e4f6),
      onSecondaryFixed: Color(0xff03121f),
      secondaryFixedDim: Color(0xffb8c8da),
      onSecondaryFixedVariant: Color(0xff293845),
      tertiaryFixed: Color(0xffeddcff),
      onTertiaryFixed: Color(0xff170a28),
      tertiaryFixedDim: Color(0xffd2bfe7),
      onTertiaryFixedVariant: Color(0xff3d3050),
      surfaceDim: Color(0xff101418),
      surfaceBright: Color(0xff414549),
      surfaceContainerLowest: Color(0xff05080b),
      surfaceContainerLow: Color(0xff1a1e22),
      surfaceContainer: Color(0xff25282c),
      surfaceContainerHigh: Color(0xff2f3337),
      surfaceContainerHighest: Color(0xff3a3e42),
    );
  }

  ThemeData darkMediumContrast() {
    return theme(darkMediumContrastScheme());
  }

  static ColorScheme darkHighContrastScheme() {
    return const ColorScheme(
      brightness: Brightness.dark,
      primary: Color(0xffe6f1ff),
      surfaceTint: Color(0xff99ccfa),
      onPrimary: Color(0xff000000),
      primaryContainer: Color(0xff95c8f6),
      onPrimaryContainer: Color(0xff000c18),
      secondary: Color(0xffe6f1ff),
      onSecondary: Color(0xff000000),
      secondaryContainer: Color(0xffb4c4d6),
      onSecondaryContainer: Color(0xff000c18),
      tertiary: Color(0xfff7ecff),
      onTertiary: Color(0xff000000),
      tertiaryContainer: Color(0xffcebbe3),
      onTertiaryContainer: Color(0xff110522),
      error: Color(0xffffece9),
      onError: Color(0xff000000),
      errorContainer: Color(0xffffaea4),
      onErrorContainer: Color(0xff220001),
      surface: Color(0xff101418),
      onSurface: Color(0xffffffff),
      onSurfaceVariant: Color(0xffffffff),
      outline: Color(0xffebf0f8),
      outlineVariant: Color(0xffbec3cb),
      shadow: Color(0xff000000),
      scrim: Color(0xff000000),
      inverseSurface: Color(0xffe0e2e8),
      inversePrimary: Color(0xff0a4c73),
      primaryFixed: Color(0xffcce5ff),
      onPrimaryFixed: Color(0xff000000),
      primaryFixedDim: Color(0xff99ccfa),
      onPrimaryFixedVariant: Color(0xff001321),
      secondaryFixed: Color(0xffd4e4f6),
      onSecondaryFixed: Color(0xff000000),
      secondaryFixedDim: Color(0xffb8c8da),
      onSecondaryFixedVariant: Color(0xff03121f),
      tertiaryFixed: Color(0xffeddcff),
      onTertiaryFixed: Color(0xff000000),
      tertiaryFixedDim: Color(0xffd2bfe7),
      onTertiaryFixedVariant: Color(0xff170a28),
      surfaceDim: Color(0xff101418),
      surfaceBright: Color(0xff4d5055),
      surfaceContainerLowest: Color(0xff000000),
      surfaceContainerLow: Color(0xff1c2024),
      surfaceContainer: Color(0xff2d3135),
      surfaceContainerHigh: Color(0xff383c40),
      surfaceContainerHighest: Color(0xff43474c),
    );
  }

  ThemeData darkHighContrast() {
    return theme(darkHighContrastScheme());
  }

  ThemeData theme(ColorScheme colorScheme) => ThemeData(
        useMaterial3: true,
        brightness: colorScheme.brightness,
        colorScheme: colorScheme,
        textTheme: textTheme.apply(
          bodyColor: colorScheme.onSurface,
          displayColor: colorScheme.onSurface,
        ),
        scaffoldBackgroundColor: colorScheme.surface,
        canvasColor: colorScheme.surface,
      );

  List<ExtendedColor> get extendedColors => [];
}

class ExtendedColor {
  final Color seed, value;
  final ColorFamily light;
  final ColorFamily lightHighContrast;
  final ColorFamily lightMediumContrast;
  final ColorFamily dark;
  final ColorFamily darkHighContrast;
  final ColorFamily darkMediumContrast;

  const ExtendedColor({
    required this.seed,
    required this.value,
    required this.light,
    required this.lightHighContrast,
    required this.lightMediumContrast,
    required this.dark,
    required this.darkHighContrast,
    required this.darkMediumContrast,
  });
}

class ColorFamily {
  const ColorFamily({
    required this.color,
    required this.onColor,
    required this.colorContainer,
    required this.onColorContainer,
  });

  final Color color;
  final Color onColor;
  final Color colorContainer;
  final Color onColorContainer;
}
