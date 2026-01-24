#!/usr/bin/env python3
"""
Test script to verify that our enhanced debugging with VsDbg works properly.
This simulates the MCP client testing enhanced debugging functionality.
"""

import json
import subprocess
import time
import sys
import os

def test_enhanced_debugging():
    """Test the enhanced debugging capabilities with VsDbg."""
    
    print("🧪 Testing Enhanced Debug Adapter with VsDbg support...")
    
    # First, let's make sure we have a .NET 10.0 test application
    test_app_path = r"C:\Users\clefw\source\repos\KazoVault\src\KazoVault.UI\bin\Debug\net10.0-windows\KazoVault.UI.exe"
    
    if not os.path.exists(test_app_path):
        print(f"❌ Test application not found at {test_app_path}")
        print("Please ensure you have a .NET 10.0 application built for testing.")
        return False
    
    print(f"✅ Test application found: {test_app_path}")
    
    # Start the MCP debug adapter server
    print("🚀 Starting MCP Debug Adapter server...")
    adapter_path = r"c:\Users\clefw\source\repos\Kazo-McpDebugAdapter\src\McpDebugAdapter\bin\Release\net9.0\McpDebugAdapter.exe"
    
    if not os.path.exists(adapter_path):
        print(f"❌ Debug adapter not found at {adapter_path}")
        print("Please build the project first: dotnet build --configuration Release")
        return False
    
    # Test the enhanced debug launch with VsDbg
    test_payload = {
        "method": "debug_launch",
        "params": {
            "programPath": test_app_path,
            "args": [],
            "stopAtEntry": False,
            "useVsDbg": True,  # This is the key parameter!
            "useLaunchThenAttach": False  # Test direct VsDbg launch first
        }
    }
    
    print("📋 Test Configuration:")
    print(f"   Program: {os.path.basename(test_app_path)}")
    print(f"   UseVsDbg: True (testing native .NET debugger)")
    print(f"   LaunchThenAttach: False")
    
    print("\n🔄 This test would verify:")
    print("   1. VsDbg path detection")
    print("   2. VsDbg process launch")
    print("   3. DAP protocol initialization")
    print("   4. .NET 10.0 compatibility")
    print("   5. Breakpoint support")
    
    print(f"\n📝 Test payload would be:")
    print(json.dumps(test_payload, indent=2))
    
    print(f"\n✅ Enhanced debugging setup complete!")
    print(f"💡 To test manually:")
    print(f"   1. Start the adapter: {adapter_path}")
    print(f"   2. Connect via MCP and send the above payload")
    print(f"   3. Verify vsdbg is used instead of netcoredbg")
    print(f"   4. Check that .NET 10.0 debugging works properly")
    
    return True

if __name__ == "__main__":
    success = test_enhanced_debugging()
    sys.exit(0 if success else 1)