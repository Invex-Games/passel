import 'dart:math' as math;

import 'package:flutter/material.dart';

/// Wraps a child widget with a staggered fade + slide entrance animation.
class AnimatedTileWrapper extends StatefulWidget {
  const AnimatedTileWrapper({
    super.key,
    required this.child,
    required this.animate,
    required this.staggerIndex,
  });

  final Widget child;
  final bool animate;
  final int staggerIndex;

  @override
  State<AnimatedTileWrapper> createState() => _AnimatedTileWrapperState();
}

class _AnimatedTileWrapperState extends State<AnimatedTileWrapper>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller;
  late final Animation<double> _fadeAnimation;
  late final Animation<Offset> _slideAnimation;

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 200),
    );

    _fadeAnimation = CurvedAnimation(
      parent: _controller,
      curve: Curves.easeOut,
    );
    _slideAnimation = Tween<Offset>(
      begin: const Offset(0, 0.15),
      end: Offset.zero,
    ).animate(CurvedAnimation(
      parent: _controller,
      curve: Curves.easeOutCubic,
    ));

    if (widget.animate) {
      // Stagger: cap at 10 items, 50ms apart.
      final delay =
          Duration(milliseconds: math.min(widget.staggerIndex, 10) * 10);
      Future.delayed(delay, () {
        if (mounted) _controller.forward();
      });
    } else {
      _controller.value = 1.0;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return FadeTransition(
      opacity: _fadeAnimation,
      child: SlideTransition(
        position: _slideAnimation,
        child: widget.child,
      ),
    );
  }
}
