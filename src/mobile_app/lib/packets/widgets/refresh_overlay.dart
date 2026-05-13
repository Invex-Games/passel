import 'package:custom_refresh_indicator/custom_refresh_indicator.dart';
import 'package:flutter/material.dart';

/// Overlay indicator shown during pull-to-refresh on the packet list.
class RefreshOverlay extends StatelessWidget {
  const RefreshOverlay({super.key, required this.controller});

  final IndicatorController controller;

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: controller,
      builder: (context, _) {
        // Hide when idle.
        if (controller.value == 0) return const SizedBox.shrink();

        final progress = controller.value.clamp(0.0, 1.0);
        final isArmed = controller.state == IndicatorState.armed;
        final isLoading = controller.state == IndicatorState.loading;
        final isComplete = controller.state == IndicatorState.complete;

        return Positioned(
          left: 0,
          right: 0,
          bottom: 16 + (20 * progress),
          child: Center(
            child: AnimatedOpacity(
              duration: const Duration(milliseconds: 150),
              opacity: progress,
              child: AnimatedScale(
                duration: const Duration(milliseconds: 150),
                scale: 0.7 + (progress * 0.3),
                child: Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                  decoration: BoxDecoration(
                    color:
                        Theme.of(context).colorScheme.surfaceContainerHighest,
                    borderRadius: BorderRadius.circular(16),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      SizedBox(
                        height: 16,
                        width: 16,
                        child: isLoading || isComplete
                            ? const CircularProgressIndicator(strokeWidth: 2)
                            : CircularProgressIndicator(
                                strokeWidth: 2,
                                value: progress,
                              ),
                      ),
                      AnimatedSize(
                        duration: const Duration(milliseconds: 200),
                        curve: Curves.easeOut,
                        child: isArmed || isLoading || isComplete
                            ? Padding(
                                padding: const EdgeInsets.only(left: 8),
                                child: Text(
                                  isLoading || isComplete
                                      ? 'Refreshing…'
                                      : 'Release to refresh',
                                  style: Theme.of(context).textTheme.bodySmall,
                                ),
                              )
                            : const SizedBox.shrink(),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}
