gcc -c -Wall -Werror -fpic -mavx -msha -msse4 -O3 /mnt/d/Projects/C#/AVDump3/AVDump3NativeLib/*.c -lrt; gcc -mavx -msha -msse4 -O3 -shared -o AVDump3NativeLib.so /mnt/d/Projects/C#/AVDump3/AVDump3NativeLib/*.o -lrt