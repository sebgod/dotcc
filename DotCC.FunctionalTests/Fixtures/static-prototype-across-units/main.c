#include <stdio.h>
int a_entry(void);
int b_entry(void);
int main(void)
{
    printf("%d %d\n", a_entry(), b_entry());
    return 0;
}
