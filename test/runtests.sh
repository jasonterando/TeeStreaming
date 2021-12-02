#!/bin/sh
mkdir -p .results
rm -r -f .results/*
dotnet test --collect:"XPlat Code Coverage" -r .results/log -l trx
result=$?
dotnet reportgenerator -reports:`find .results -name coverage.cobertura.xml` -targetdir:.results/reports
if [ $result = 0 ]; then
    echo "All tests passed!"
else
    echo "Some tests failed :("
fi
exit $result