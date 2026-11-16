"""Installed application entry point; imports only from the fixed system path."""
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, "/usr/lib/fancontroller")
from fancontroller.cli import main

raise SystemExit(main())
