// CrossWin Flutter sample: a counter plus two deliberate failures, so the agent diagnostics
// (crosswin events / ui / input) can be checked on an emulator or a real phone.
import 'package:flutter/material.dart';

void main() => runApp(const HelloApp());

class HelloApp extends StatelessWidget {
  const HelloApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'CrossWin Flutter sample',
        theme: ThemeData(colorSchemeSeed: Colors.indigo),
        home: const HomePage(),
      );
}

class HomePage extends StatefulWidget {
  const HomePage({super.key});

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  int _taps = 0;
  bool _overflow = false;

  void _save() {
    // Deliberate bug for diagnostics demos: an unhandled exception in a tap handler.
    throw StateError('Cannot save: no file selected');
  }

  @override
  Widget build(BuildContext context) {
    debugPrint('CROSSWIN_READY taps=$_taps');
    return Scaffold(
      appBar: AppBar(title: const Text('CrossWin Flutter sample')),
      body: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Taps: $_taps', style: Theme.of(context).textTheme.headlineMedium),
            const SizedBox(height: 16),
            FilledButton(onPressed: () => setState(() => _taps++), child: const Text('Tap me')),
            const SizedBox(height: 8),
            OutlinedButton(onPressed: _save, child: const Text('Save')),
            const SizedBox(height: 8),
            OutlinedButton(
              onPressed: () => setState(() => _overflow = !_overflow),
              child: const Text('Toggle wide row'),
            ),
            if (_overflow)
              // Deliberate layout bug: a fixed-width row wider than any phone (RenderFlex overflow).
              const Row(children: [SizedBox(width: 900, child: Text('This row is too wide for the screen'))]),
          ],
        ),
      ),
    );
  }
}
