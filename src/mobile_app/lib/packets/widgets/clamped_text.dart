import 'package:flutter/material.dart';

/// [text] clamped to [maxLines], with a "Show more" button that expands it
/// in place (and "Show less" to clamp it again).
///
/// The button only appears when the text really needs more lines than
/// [maxLines] at the width it is given, so short notes look as if nothing
/// were clamped.  Expansion is widget state, so it survives the feed moving
/// the tile.
class ClampedText extends StatefulWidget {
  const ClampedText(
    this.text, {
    super.key,
    this.maxLines = 8,
    this.style,
  });

  final String text;
  final int maxLines;
  final TextStyle? style;

  @override
  State<ClampedText> createState() => _ClampedTextState();
}

class _ClampedTextState extends State<ClampedText> {
  bool _expanded = false;

  @override
  Widget build(BuildContext context) {
    final style = DefaultTextStyle.of(context).style.merge(widget.style);

    return LayoutBuilder(
      builder: (context, constraints) {
        final clamps = _needsMoreLines(context, style, constraints.maxWidth);
        final clamped = clamps && !_expanded;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              widget.text,
              style: style,
              maxLines: clamped ? widget.maxLines : null,
              overflow: clamped ? TextOverflow.ellipsis : null,
            ),
            if (clamps)
              TextButton(
                onPressed: () => setState(() => _expanded = !_expanded),
                style: TextButton.styleFrom(
                  visualDensity: VisualDensity.compact,
                  padding: const EdgeInsets.symmetric(horizontal: 8),
                ),
                child: Text(_expanded ? 'Show less' : 'Show more'),
              ),
          ],
        );
      },
    );
  }

  /// Whether the text needs more than [ClampedText.maxLines] lines at
  /// [maxWidth], laid out the way [Text] will lay it out.
  bool _needsMoreLines(BuildContext context, TextStyle style, double maxWidth) {
    final painter = TextPainter(
      text: TextSpan(text: widget.text, style: style),
      maxLines: widget.maxLines,
      textDirection: Directionality.of(context),
      textScaler: MediaQuery.textScalerOf(context),
    )..layout(maxWidth: maxWidth);
    final exceeds = painter.didExceedMaxLines;
    painter.dispose();
    return exceeds;
  }
}
