/* __tm_names: the C locale's day and month names, for asctime and strftime. The wat target's
   libc (WatLibc), compiled with the program. */
#include <__tm.h>

const struct __tm_names_t __tm_names = {
    { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" },
    { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" },
    { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" },
    { "January", "February", "March", "April", "May", "June",
      "July", "August", "September", "October", "November", "December" },
};
