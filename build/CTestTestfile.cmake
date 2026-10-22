# CMake generated Testfile for 
# Source directory: D:/Projects/Desktop/Native
# Build directory: D:/Projects/Desktop/build
# 
# This file includes the relevant testing commands required for 
# testing this directory and lists subdirectories to be tested as well.
if(CTEST_CONFIGURATION_TYPE MATCHES "^([Dd][Ee][Bb][Uu][Gg])$")
  add_test([=[bridge_abi_lifecycle]=] "D:/Projects/Desktop/build/Debug/bridge_smoke.exe")
  set_tests_properties([=[bridge_abi_lifecycle]=] PROPERTIES  _BACKTRACE_TRIPLES "D:/Projects/Desktop/Native/CMakeLists.txt;107;add_test;D:/Projects/Desktop/Native/CMakeLists.txt;0;")
elseif(CTEST_CONFIGURATION_TYPE MATCHES "^([Rr][Ee][Ll][Ee][Aa][Ss][Ee])$")
  add_test([=[bridge_abi_lifecycle]=] "D:/Projects/Desktop/build/Release/bridge_smoke.exe")
  set_tests_properties([=[bridge_abi_lifecycle]=] PROPERTIES  _BACKTRACE_TRIPLES "D:/Projects/Desktop/Native/CMakeLists.txt;107;add_test;D:/Projects/Desktop/Native/CMakeLists.txt;0;")
elseif(CTEST_CONFIGURATION_TYPE MATCHES "^([Mm][Ii][Nn][Ss][Ii][Zz][Ee][Rr][Ee][Ll])$")
  add_test([=[bridge_abi_lifecycle]=] "D:/Projects/Desktop/build/MinSizeRel/bridge_smoke.exe")
  set_tests_properties([=[bridge_abi_lifecycle]=] PROPERTIES  _BACKTRACE_TRIPLES "D:/Projects/Desktop/Native/CMakeLists.txt;107;add_test;D:/Projects/Desktop/Native/CMakeLists.txt;0;")
elseif(CTEST_CONFIGURATION_TYPE MATCHES "^([Rr][Ee][Ll][Ww][Ii][Tt][Hh][Dd][Ee][Bb][Ii][Nn][Ff][Oo])$")
  add_test([=[bridge_abi_lifecycle]=] "D:/Projects/Desktop/build/RelWithDebInfo/bridge_smoke.exe")
  set_tests_properties([=[bridge_abi_lifecycle]=] PROPERTIES  _BACKTRACE_TRIPLES "D:/Projects/Desktop/Native/CMakeLists.txt;107;add_test;D:/Projects/Desktop/Native/CMakeLists.txt;0;")
else()
  add_test([=[bridge_abi_lifecycle]=] NOT_AVAILABLE)
endif()
