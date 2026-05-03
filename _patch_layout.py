# -*- coding: utf-8 -*-
"""Replace Mirror+EQ two-column Grid with stacked golden-ratio strip + full-width EQ."""
from pathlib import Path

p = Path(__file__).resolve().parent / "MainWindow.xaml"
text = p.read_text(encoding="utf-8")
start_marker = "                                                        <!-- Mirror outputs + Graphic EQ -->"
frag_path = Path(__file__).resolve().parent / "_mirror_eq_fragment.xml"
NEW_CHUNK = frag_path.read_text(encoding="utf-8").rstrip() + "\n"

i = text.index(start_marker)
close_grid = "\n                                                        </Grid>"
idx_g = text.index(close_grid, i)
tail = text[idx_g:]
idx_sp = tail.index("\n                                                    </StackPanel>")
tail2 = tail[idx_sp + len("\n                                                    </StackPanel>") :]

text2 = text[:i] + NEW_CHUNK + "\n                                                    </StackPanel>" + tail2
p.write_text(text2, encoding="utf-8")
print("ok", i, idx_g, idx_sp)
